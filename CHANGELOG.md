# 更新日志（Andzip）

> 格式遵循需求文档 §11.2：每条包含版本号、日期、分类（新增 / 修改 / 修复 / 变更）、关联需求编号、测试结果。最新版本置顶。

## [0.11.1] - 2026-10-13（修复：「选择解压路径」里那棵根本展不开的目录树，换成系统组件）

> 版本号 0.11.0 → **0.11.1**（修的是坏掉的东西，动第三位）。

### 修复

- **「选择解压路径」右边的目录树展不开**。它是手写的 `TreeView`，而 `ItemTemplate` 用的是
  扁平 `FuncDataTemplate`，没有把 `FolderNode.Children` 绑成子项来源 —— 于是**连展开三角
  都不出现**，界面上永远只有 `C:\` 与 `D:\` 两行。懒加载的代码（`EnsureChildrenLoaded`）
  写了，只是接不上。
- 顺带一处假异步：`CreateFolderAsync` 里那句 `await Task.CompletedTask` 是为了让
  `async void` 事件处理器闭嘴而加的，改成同步的 `CreateFolder()`。
- **`build.ps1` 重发同一个版本号时会失败**：`Move-Item -Force` 在 Windows 上**不覆盖**
  已存在的目标文件（`-Force` 只压 read-only），于是报"当文件已存在时，无法创建该文件"。
  这一版之前已经撞过两回（0.10.0 与 0.11.1），每次都是靠改版本号绕过去的 —— 那是掩盖不是修。
  现在先删掉旧产物再移动。

### 变更

- **浏览一律走系统的文件夹选择对话框**，不再自己画目录树。参考软件那块是系统的 shell 树
  （`SysTreeView32`），**嵌不进 Avalonia 的窗口**，所以"对话框里内嵌一棵带图标的 shell 树"
  和"用系统组件"这两件事不能同时要 —— 用户选的是后者。
  现在点「浏览」弹的是 `IFileOpenDialog` 的选文件夹模式（比参考软件那个还新），
  导航窗格带图标、能一路展开到任意深度。
- **系统对话框打开时定位在当前目标路径**（`SuggestedStartLocation`）。以前它从"我的文档"
  开始翻，用户其实只想往下走一层。
- 快捷位置（桌面 / 我的文档 / 我的图片 / 我的电脑）从竖排一列改成横排一行 ——
  旁边那块树没了，竖排会留一大片空白。窗口随之从 760×560 收到 700×292。
- 快捷位置按钮在路径为空时**不再添加**（比如某台机器上"我的图片"被重定向没了）：
  摆一个点了不会有反应的按钮，比少一个按钮更糟。

### 测试结果

- 全部门禁绿：原生内核 532 项、Core 360、界面 **25**、P/Invoke 外壳 69，共 **986** 项断言。
- 界面测试新增两条：`ExtractPathDialogHasNoHandRolledFolderTree`（钉"别再手写一棵回来"，
  同时确认「浏览」在且可用）与 `ExtractPathDialogStartsAtTheArchiveFolder`
  （默认目标路径就是包所在目录）。
- **真机点过「浏览」**：弹出来的是系统的选文件夹对话框 —— 左侧导航窗格带图标
  （此电脑 / 视频 / 图片 / 文档 / 下载 / 音乐 / 桌面 / 本地磁盘 C: D:），
  面包屑已经定位在 `AppData > Local > Temp > andzip-crashcheck`，标题是我们给的「目标路径(D)」。
  对话框本身也重新截图核对过：没有裁切，按钮是 确定(O) / 取消，无 `(&X)` 残留。

## [0.11.0] - 2026-10-13（新增：拖拽进主窗口；变更：默认包名跟着选中项走、格式下拉不再列写不出来的格式）

## [0.11.0] - 2026-10-13（新增：拖拽进主窗口；变更：默认包名跟着选中项走、格式下拉不再列写不出来的格式）

> 版本号 0.10.0 → **0.11.0**（拖拽是小功能 → 第二位 +1、第三位清零）。

### 新增

- **主窗口接受从资源管理器拖进来的东西**（FR-1 / FR-2）。规则只有一条判断，放在
  `ShellTasks.RouteDroppedPaths`：
  **单个**且扩展名是引擎读得开的包 → 就在这个窗口里打开它（与右键「用 Andzip 打开」同一条任务，
  不另起一套判断）；其余（普通文件、文件夹、一次拖多个）→ 开「新建压缩文件」对话框并把它们
  填进输入列表。
  这里**不是**"添加到当前打开的包里" —— 本工具不能往已有归档写成员（见未完成清单），
  所以包已经打开时拖进来的东西同样是压成一个新包。

### 变更

- **「文件名」默认值跟着选中的东西走**：压一个 `photo.jpg` 得到的是同目录的 `photo.zip`，
  不再是 `新建压缩.zip`；**选中多个**（或还没有任何输入）时才用「新建压缩」，
  目录取它们的共同父目录，各不相同才退回「设置 → 压缩 → 输出目录」。
  规则做成纯函数 `ShellTasks.SuggestedArchivePath`，五种情形都有单测。
- **压缩格式下拉框只列引擎真能创建的格式**：ZIPX / EXE / LZH / ISO / ZPAQ 撤出列表。
  它们以前是照参考软件摆着的只读候选，选中之下只能开始一句「该功能暂未实现」——
  用户的话是"列出来就一定要实现"。列表按运行时的 `CanCreate` 过滤而不是写死一张子表，
  所以哪天引擎能写 ISO 了那一行自己回来。这五项仍然留在 `ArchiveFormatOption.All` 里
  （扩展名归类等用途要），12 项候选里创建对话框现在给 7 项。
- 工具栏「新建」与右键「压缩到…」共用同一套预填逻辑，参数从单个 `seedPath` 变成
  `seedPaths` 列表（拖一次可以进来好几个）。

### 测试结果

- 全部门禁绿：原生内核 532 项、Core **360**、界面 **23**、P/Invoke 外壳 69，共 **984** 项断言。
- 新增断言里值得点名的两条：`Creatable_FormatsAreAllCreatableAndKeepTheReferenceOrder`
  钉的是"没有任何一项是点下去只会报暂未实现的"，而不是数量；
  `IsReadablePath_NeverCallsAFolderAnArchive` 钉的是"真拖一个叫 `photos.zip` 的文件夹进窗口，
  那是要压的东西，不是要看的东西"。
- **真机截图核对过**：右键一个 `note.txt` 选「压缩到…」→ 对话框里「文件名」是
  `...\note.zip`（跟着源文件走，也在它旁边），按钮是 添加(A) / 浏览(B)... / 开始(S)，
  没有 `(&X)` 残留。
- **拖拽本身需要你手验**：Avalonia 的 `IStorageItem` 被标成不允许用户代码实现，
  headless 里造不出一次真实的文件拖放，所以操作系统那一层没有自动化覆盖。
  判断逻辑（哪个路径该开、哪批该压）在 Core 里测过了，没测到的是"拖进来到底有没有反应"。

## [0.10.0] - 2026-10-13（变更：右键菜单重做、进度窗口报剩余时间；修复：对话框上露出的 `(&X)`）

## [0.10.0] - 2026-10-13（变更：右键菜单重做、进度窗口报剩余时间；修复：对话框上露出的 `(&X)`）

> 版本号 0.9.12 → **0.10.0**。这一版起按新规则编号：**每一位都是 0-9，满了向左边进一**
> （不是 SemVer，第三段不会跑到 10、11、12）。这一轮全部是用户直接指出的问题，
> 六条一起改：右键菜单的动词集合、"打开"名不副实、进度条没有剩余时间、
> 工具栏两个没用的下拉箭头、按钮上露出的助记符。

### 变更

- **右键一个压缩包只有三项**：用 Andzip 打开 / 解压 / 解压并删除。
  「压缩为 ZIP」「压缩到…」不再出现在压缩包身上 —— 把 zip 再压成 zip 不是谁想要的功能。
  实现方式是给 `*\shell` 那两条动词写上注册表 `AppliesTo`
  （`NOT (System.FileExtension:=.zip OR …)`，49 个扩展名），**不是**我们自己的猜测：
  本机往注册表写一个值，再用 `Shell.Application` 枚举 `box.zip` 与 `note.txt` 的动词表，
  前者消失、后者仍在，才算确认了这条机制存在。
- **右键的其他文件与文件夹只剩两项**：「压缩为 ZIP」与「压缩到…」。
  「压缩为 7Z」撤掉 —— 要压 7Z 走「压缩到…」在对话框里选格式。
- **「智能解压」并进「解压」，界面上不再有这个词**。落点规则原样保留（单文件直接解出来、
  整棵单子树就放那个文件夹、零散多个才套一层以包名命名的文件夹），只是不再有两个动词让用户
  分不清该点哪个。`ShellTask` 从五条压到四条：`AutoExtract` / `ExtractHere` 合并成 `Extract`，
  `CompressSevenZip` 撤掉。老命令行（`/task=auto-extract`、`/task=extract-here`、`/task=compress-7z`、
  裸 `/extract`）**仍然认**，映射到今天最接近的一条。
- **工具栏「打开」「解压」右侧的下拉箭头删掉了**。它们确实没作用：「打开」的菜单里
  「最近的文件」根本没有这个功能、永远灰着，另一条与主按钮同命令；「解压」的菜单只有一条，
  点的就是主按钮那一条。
- **进度窗口现在报百分比与预估剩余时间**（`正在测试...  45%  12.3 MB / 27.4 MB  ·  剩余 0:14`）。
  剩余时间用「已用 / 已完成」线性外推，前 3% 不报（样本太小，估算会从 2 分钟跳到 40 分钟，
  一个乱跳的数字比没有更糟）；估出一天以上也不报。计时从**第一帧进度**开始，不是从窗口构造 ——
  中间可能隔着一次口令询问，把用户想口令的几十秒算进速度里会得到荒谬的结果。
  时长写成 `分:秒` 而不是"14 秒"，是为了不必为一套时长单位做本地化。

### 修复

- **右键「用 Andzip 打开」以前会解压**。那条动词的命令行不带开关，于是行为落回
  「常规 → 双击压缩包文件时」—— 勾了"直接解压"的用户点「打开」，包就被解开了，名字说的和做的不一样。
  现在双击与右键是两条注册表键：`shell\open` 仍然不带开关（改设置不用重写 ProgID）但加上
  `ProgrammaticAccessOnly` 不进菜单，菜单上那条是 `shell\andzip-open`，命令行写死 `/task=open`。
  真机在 `double_click_action = extract` 这台机器上两条都验过：右键打开 → 出主窗口、磁盘上不落地；
  双击同一个包 → 只弹进度条并解出文件。
- **对话框上的按钮把 Win32 助记符原样显示给用户**：「确定(&O)」「添加(&A)」「浏览(&B)…」
  「设置密码(&P)…」「开始(&S)」「测试压缩文件(&T)」等等。Avalonia 的 SimpleTheme 不解析 `&`，
  而 0.9.11 只有菜单栏与工具栏调了剥离。现在 App 层取控件文案统一走 `lang.Label(key)`
  （95 处），`MessageWindow` 的标题与正文也在自己的边界上剥一次，不要求每个调用点记得。
  语言包保持 Win32 写法不动，将来接真正的访问键时不用回头改文案。

### 新增

- **右键「解压并删除」**：与「解压」同一套落点规则，成功后删包，**分卷包连卷一起删**
  （只删主卷会留下一地读不了的 `.z01`）。取消或失败时包必须还在。
- 界面测试 17 → **20** 条：两条"任何窗口都不许露出 `(&X)`"的全量断言（含设置页九个页面逐页走一遍），
  一条进度窗口百分比 + 剩余时间断言。Core 341 → **349**，含 5 条 `AppliesTo` 查询串的形态断言
  与一条分卷删除断言。

### 测试结果

- 全部门禁绿：原生内核 532 项、Core 349、界面 20、P/Invoke 外壳 69，共 **970** 项断言。
- **右键菜单是真机枚举过的**，不是"写完注册表就当生效"：`Shell.Application` 读出
  `box.zip` 的动词表 = 用 Andzip 打开 / 解压 / 解压并删除（没有任何压缩项），
  `note.txt` = 压缩为 ZIP / 压缩到… 仍在。退役的 `auto-extract` / `extract-here` / `compress-7z`
  键已从注册表里删掉。
- 一个坑记在这里：资源管理器的动词表**有缓存**，写完注册表立刻枚举会读到半套菜单
  （那次只看到「用 Andzip 打开」一条）。差点把缓存当成 bug 去改代码。

## [0.9.12] - 2026-10-12（修复：点「测试压缩文件」就闪退）

> 版本号 0.9.11 → **0.9.12**，BuildNo 20 → **21**。上一轮刚上线的完整性检测，用户一点就崩。
> 而那一版的 14 条界面测试**全绿** —— 这一轮补上了界面测试看不见的那一层。

### 修复

- **`Ctrl+T` / 工具栏「测试」打开进度窗口后进程直接消失**。事件日志里的栈是决定性的：
  `Progress<T>` 捕获到的 `SynchronizationContext`，它的 `Post` 是**基类默认实现（丢线程池）**，
  于是引擎线程直接改 `ProgressBar`，Avalonia 抛 `Call from invalid thread`，没人接，进程带走。
  现在改成 `ProgressWindow.CreateSink()` 显式走 `Dispatcher.UIThread.Post`，六个调用点全部换掉 ——
  **"回到 UI 线程"不能寄托在环境上下文上**。压缩、解压、新建、检测、修复五条路径同受其益。
- **`RepairWindowOffersThreeDistinctModes` 的一条断言有 CS8631 可空性告警**，
  换成 `IEnumerable` 版重载。顺手的事，但门禁应当零告警。

### 新增

- **`tests/Andzip.App.UI.Tests/ProgressWindowThreadingTests.cs`**（界面测试 14 → **17**）。
  断言的不是"有没有炸"，而是**回调落在哪个线程**：上报之后先确认窗口还没动（只是被排进队列），
  `Dispatcher.UIThread.RunJobs()` 之后才确认它动了。含一条端到端：真包、真引擎、真进度窗口、
  后台线程跑完整性检测。
- **`azcore --prealloc` / `--share-locked`** 之外没有新增内核能力，`AZ_ABI_VERSION` 仍是 2。

### 关于"为什么自动化没抓到"

**Avalonia 的 headless 平台不检查线程亲和性** —— 从池线程改控件在那里不会抛。
所以 0.9.11 那 14 条测的是"控件在不在、可不可用"，这一层它结构上就看不见。
复盘（含两条一开始走错的诊断、以及"验证时跑的是上一次 publish 的旧 exe"这个坑）
写在 `docs/开发记录.md` 第 19 条。

### 测试结果

- 全部门禁绿：原生内核检查、Core 单元、界面 headless、P/Invoke 外壳、静态检查。
- **真机复现过的那一下现在不崩了**：`box.zip` → `Ctrl+T` → 「测试完成 / 所有成员校验通过，
  没有发现损坏」，进程存活，Windows 应用程序日志 0 条新崩溃（1026）。

## [0.9.11] - 2026-10-12（新增：预分配磁盘、压缩不独占源文件、完整性检测上界面、修复损坏的 ZIP）

> 版本号 0.9.10 → **0.9.11**，BuildNo 19 → **20**。这一轮把"能做但看不见"的东西全部接到了界面上，
> 并且第一次有了自动化界面测试 —— 它当场逮到一个点开就崩的生产 bug。

### 新增

- **压缩时预分配磁盘空间**（`设置 → 压缩`，默认关）。内核在写入前按**未压缩总量**用
  `SetFileInformationByHandle(FileAllocationInfo)` 占住簇。**刻意不用 `SetFileValidData`** ——
  那个要 `SE_RENAME_NAME` 权限，并且会把别人删过的簇标成我们的，是一个数据泄露面。
  实测：115.9 MB 可压缩语料跑 `-x 9`，压缩包才写进 5 KB 时可用空间已经掉了 116 MB；
  同一条件下不开这个开关，可用空间一点不动。
- **压缩时不独占源文件**（默认关）。成员以 `FILE_SHARE_READ | FILE_SHARE_WRITE` 打开，
  压缩期间别的程序还能继续写/删这些文件。
- **压缩包完整性检测上界面**：工具栏「测试」按钮 + `文件 → 测试压缩文件`（Ctrl+T）。
  结果分两类文案 —— **数据坏了**（引擎点名的成员原样转达）与**根本没读成**
  （缺文件 / 口令错 / 格式不支持）。把后者写成"包坏了"是误导：前者要修，后者要去找文件。
- **`Core/Archive/ZipScanner.cs`**：自己解析 EOCD / ZIP64 定位器与记录 / 中央目录 / 局部文件头游走。
  不借引擎是故意的 —— 修复要处理的正是引擎读不出来的包。三处细节值得记：
  ZIP64 扩展里**只有被 `0xFFFFFFFF` 顶掉的那几个字段才会出现**，读错一位就是把大小当偏移用；
  SFX 前导不靠猜，用"候选前导 + 中央目录偏移必须真的落在中央目录签名上"验证；
  成员名允许 UTF-8 高字节（中文文件名是常态）。
- **修复损坏的 ZIP**（`工具 → 修复压缩包`，三种模式由用户选）：重建中心目录 /
  续接分卷与被截断的包 / 能修多少修多少（逐成员真解压再重打包）。
  **输出永远不是原包**（同路径直接拒绝），不确定的一律跳过并给出原因。
  验收标准是**别的工具能用**：修好的包一律用 `System.IO.Compression.ZipFile` 读回来逐字节比对。
- **`tests/Andzip.App.UI.Tests`**：Avalonia headless 界面测试 14 条（含 3 张渲染截图）。
  装配的是真实窗口，不是控件近似物。
- `azcore` 增加 `--prealloc` / `--share-locked`：这类行为只有能被命令行量化，才谈得上验证。

### 修复

- **读不到的成员把成功的压缩报成了失败**。`az_writer_finish` 原先返回 `AZ_ERROR_IO`，
  可磁盘上那个包其实是完整的 —— 报错代码给的是"失败"，文件却是好的，用户两头都不信了。
  现在改成：跳过、计数、点名，走 `az::Warn` 返回成功，界面在结果里列出跳过了哪几个。
- **`RepairWindow` 一点单选钮就崩**。三个 `IsCheckedChanged` 处理程序的闭包捕获了 `for` 的循环变量，
  于是全都看到 `i == 3`，`_modes[3]` 直接 `IndexOutOfRangeException`。
  这是自动化界面测试第一次跑就抓出来的 —— 纯逻辑测试看不见它，因为崩在事件处理程序里。

### 变更

- **`AZ_ABI_VERSION` 1 → 2**：`az_create_options` 尾部多了 `preallocate` / `share_locked` 两个字段。
  每个 options 结构都自带 `sizeof`，老调用方会被 `AZ_ERROR_INVALID_ARG` 挡下而不是被错读，
  但版本号还是得动，因为外壳会断言它。
- **`-ExtraCodecs` 现在能构建了**：`7-Zip-zstd-master.zip` 回来了，zstd 的 7-Zip 胶水以
  `ZstdFork{Decoder,Encoder,Register}` 的名字与原版 7-Zip 自带的 `NCompress::NZstd` 只读解码器并存
  （直接覆盖会崩在 `ZipHandler.cpp` 的 C2039 上）。`ICoder.h` 的那处补丁也搬进了
  `vendor-sources.ps1` —— 手改会在下一次 vendor 时被整棵删掉。
- **撤回上一轮的一个说法**：当时报告 LZ4 / LZ5 / Lizard / Brotli 已"逐字节往返验证通过"，
  **那是错的，这五个方法一个都写不出来**。内核确实把它们登记成了编码器（`azcore codecs` 看得到，
  537 项原生检查全过），但 7z 的写入路径对它们一律 `E_INVALIDARG`；同样不在
  `7zHandlerOut.cpp` 方法表里的 `-mm=Copy` 更是直接段错误。所以界面上不提供这五个方法，
  `CompressionMethodOption` 的表里也没有它们。

### 明确没做、并写明理由的

- **不承诺"读取被独占的文件"**。实测：另一个进程以 `share=None` 持有时，普通打开与
  `FILE_FLAG_BACKUP_SEMANTICS` 都返回 `ERROR_SHARING_VIOLATION(32)` —— 不是权限不够，
  是 Windows 不让。能做的只有跳过并上报，界面文案照此写。
- **7z 的写入路径为什么拒绝登记过的编码器**：本轮插桩没能定位（DLL 写 stderr 在这个控制台里看不见，
  改写成文件也没有产出），下一步要挪进原生测试套件里查。在那之前这五个方法不进表。
- **修复只支持 ZIP**：7z 的头是编码过的一整块，残缺后无法可靠重建。
- **模式二不承诺续接后的残缺成员可用**：它按实际剩下的字节写入中心目录，解压那一个成员会报错，
  换来的是前面那些成员仍然打得开。

### 测试

- 原生 532 项、Core 341 项、界面 14 项、P/Invoke 外壳 69 项，共 **956 项全绿**；
  `build.ps1` 完整流水线（发布 + 裁剪 + 内嵌内核）产出 19.5 MB 单文件。
- 真实窗口截图核对：主窗口（「测试」可用而 添加/删除/扫描 灰掉，符合没有打开包的状态）、
  设置 → 压缩页（两个新开关与说明在真实字体/DPI 下排版正常）、修复对话框。
- 语言包 300 键 × 中英两份，新增键全部进 `LangKeyCoverageTests` 的钉住列表。

## [0.9.10] - 2026-10-12（新增：设置 → 高级页、安全页 —— 九个设置页全部实装完毕）

> 版本号 0.9.9 → **0.9.10**，BuildNo 18 → **19**。设置窗口至此没有占位页了。

### 新增
- **设置 → 高级页**三项，全部接得上线：
  - **标题栏显示完整路径** —— 立刻改标题，不用重开包。三处 `Title = …` 收成一个
    `ArchiveTitle(path)` 方法：分开写迟早分叉，切视图时标题会突然变样。
  - **临时文件夹** —— 只用于拼接 WinZip 风格分卷（`name.z01 + name.zip`）；
    7z 风格由引擎直接读兄弟卷，不落临时文件。指定目录不可用（被删、没权限、压根不是目录）时
    **退回系统临时目录而不是抛异常**：分卷包打不开，跟临时目录设在哪儿比起来是重要得多的事。
  - **导出设置…** —— 把 `settings.ini` 原样复制到用户选的位置。按钮跟着文件在不在走，
    首次运行还没写过设置时是灰的。
- **设置 → 安全页**：会话内口令记忆。
  - 打开一个加密包时输过一次，解压它就不用再问；清单里那个口令不对（包换过口令）时会
    **先忘掉再问**，不会拿同一个错口令去撞第二遍。
  - 关掉这一项会**连带清掉已记住的**——只停"新增"而留着旧数据，用户以为没了、其实还在内存里。
  - 页面上显示当前记住了几个、并能一键清除。
  - **口令只留在内存，进程退出即消失**：不写设置文件、不写注册表。
- `Core/Archive/PasswordCache.cs`：按完整路径记、**忽略大小写**（Windows 文件系统本来就不区分，
  区分了会对同一个包问两遍口令，用户视角就是"记了个寂寞"）。
- `ArchiveEngine.TempDirectory` / `ResolveTempDirectory()`：与 `NumThreads` 一样的进程级设置，
  在 `App` 启动时一次性接上设置文件，比让每个用到的地方各自去读更难漏。
- 语言包新增 16 个键，两侧各 **273 键**、键集与占位符一致。

### 明确没做、并写明理由的
- **跨重启的「口令管理器」**：要做对得用操作系统级凭据保护（Windows 上是 DPAPI），
  那需要一个 Windows 专用的 NuGet 依赖，而本项目还要在 Linux 上构建；
  把口令明文写进 `settings.ini` 不是它的简化版，是一个谁都会踩的坑。
  这句话原样写在设置页上，而不是留一块空白让人以为没加载出来。
- **高级页其余各项**：「删除时移入回收站」「允许删除已打开的压缩包」依赖还不存在的删除功能；
  「输出文件时预分配磁盘空间」「文件被占用时仍然压缩」是引擎侧写入策略，界面勾不动；
  「退出子进程时删除临时文件」—— 本程序不留子进程，临时文件在 `SingleFileArchive.Dispose` 就删了；
  「启动时显示开始画面」「压缩/解压使用模态框」描述的正是现在的行为，做成开关等于凭空造两个没人会关的选项。
- **查看页其余各项**（0.9.9）：「列设置」「图像预览」「压缩文件注释」「置顶」「颜色设置」对应的功能本身还不存在。

### 变更
- 新增 `App/Session.cs` 承载进程级会话状态。原本把它挂在 `App` 类上，但在
  `namespace Andzip.App` 内部写 `App.Passwords` 时编译器会先把 `App` 解析成命名空间本身 ——
  一个名字响亮、不会撞车的容器比跟解析规则较劲省事。
- `LabeledRow` / `Check` / `Hint` 上提到 `SettingsWindow` 一级：压缩、高级两页都要同样的行形状。

### 验证
- `dotnet build Andzip.slnx`：**0 警告 0 错误**。
- `dotnet test`：**317 / 317**（新增 `PasswordCacheTests` 9 条：大小写不敏感命中、
  空口令不记、关掉后旧数据不再命中、`Forget`/`Clear` 计数、临时目录未设/已设/不可用三种解析；
  以及 `ViewDefaults`、`ExtractionDefaults` 的往返与缺省各若干）。
- 语言包键集核对：两侧各 **273 键**，无缺失 / 无重复 / `${占位符}` 一致，
  除语言文件自身用到的 `LANG_NAME` / `DLG_FONT_*` 外没有未引用键。
- 设置页与口令提示、标题栏、导出对话框是 GUI，**按约定由用户手动验证**
  —— 见 `docs/开发记录.md` 第九节第 32–35 条。

## [0.9.9] - 2026-10-12（新增：设置 → 查看页、设置 → 语言页）

> 版本号 0.9.8 → **0.9.9**，BuildNo 17 → **18**。

### 新增
- **设置 → 查看页**五项，全部接得上线：显示工具栏、显示状态栏、显示树形目录、
  打开压缩包时默认用图标视图、自动展开树里的文件夹。
  - 工具栏**立刻生效**（主窗口在设置对话框关掉后重新应用一次）；树、状态栏、视图样式
    是压缩包视图的构造期决定，所以关掉设置窗口时会**重画一次当前视图**，不用重开包。
  - 重画时保留用户原来停在哪一面：正看着首页就别把人扔进压缩包视图。
  - 关掉树是把 `300,4,*` 两列宽度一起归零 —— 只藏控件不够，Grid 仍会留下拖不动也点不到的空白。
- **设置 → 语言页**：候选项来自**实际加载到的语言包**（`LangCatalog.Languages`），
  显示名用各包自己的 `LANG_NAME`。程序目录 `langs/` 下多放一份 `.lang`，这一页就多一项
  且选了真能切 —— 不会出现"列表里有、切过去又回退"的假选项。
  - 当场翻 `LangCatalog`，之后新建的界面立刻是新语言；已画好的要重启，
    **页面上如实写明**，不做"看起来全变了"的假动作。
  - 设置里存了个当前不存在的语言代码（换机器、删了 `langs/`）时，下拉框停在列表第一项
    而不是留一个空白框。
- `Core/Settings/ViewDefaults.cs` + `SettingKeys.Language`；`App` 改成**先读设置再建语言包**，
  否则语言这一项永远来不及生效。
- 语言包新增 11 个键，两侧各 **257 键**、键集与占位符一致。

### 变更
- 参考软件查看页上的「列设置」「图像预览」「压缩文件注释」「置顶」「颜色设置」「视图样式子菜单」
  **没做**：前四个对应的功能本身还不存在，摆一个勾了什么都不发生的复选框比不做更糟。
  记进 `docs/开发记录.md` 第五节，免得下次有人以为是漏掉了。

### 验证
- `dotnet build Andzip.slnx`：**0 警告 0 错误**。
- `dotnet test`：**308 / 308**（新增 `ViewDefaults` 三条 + 语言键缺省一条；
  其中「关掉工具栏后重载仍是关掉的」这条专门盯 `GetBool` 缺省为 true 与存储值 "0" 的组合 ——
  写错就会变成"关不掉"）。
- 设置页、工具栏显隐、语言切换是 GUI，**按约定由用户手动验证**
  —— 见 `docs/开发记录.md` 第九节第 28–31 条。

## [0.9.8] - 2026-10-12（新增：设置 → 解压页）

> 版本号 0.9.7 → **0.9.8**，BuildNo 16 → **17**。

### 新增
- **设置 → 解压页**（原来只有占位文案），三个开关全部真正生效：
  - **智能解压时，单个文件也放进新建的文件夹** —— 默认关。勾上后 `photo.jpg.gz` 得到
    `photo\photo.jpg` 而不是直接一个 `photo.jpg`；文件夹名取自**包里那个文件**（去掉扩展名），
    不是压缩包名，且重名时照样走 `名称 (2)` 避让。
  - **解压后打开文件夹** —— 只在真的解完之后打开；失败或取消都不会弹出一个装了一半的目录
    （那比不打开更容易让人误以为东西都在里面）。
  - **解压完成后不关闭进度窗口** —— 只作用于主窗口的「解压文件」。
- `Core/Settings/ExtractionDefaults.cs`：与 `CompressionDefaults` 同样的理由，缺省值集中一处、
  可被测试钉住。
- 语言包新增 7 个键，两侧各 **246 键**、键集与占位符一致。

### 变更
- `ExtractionLayout.Inspect` 现在返回 `ArchiveShape(Kind, RootName)` 而不是裸枚举：
  单文件情形要建同名文件夹就得知道**那个文件叫什么**，而它跟压缩包名并不总是一致。
  让判定顺手把名字带出来，比让调用方回去翻 `listing.Entries[0]` 又假设一次"只有一个条目"要好。
- `Destination` 多一个 `singleFileToFolder` 可选参数，只影响单文件那一支；
  另两种形状传与不传结果相同（有测试钉住）。
- 参考软件解压页上的「一次性解压 TGZ/TBZ」「自动检测 Macintosh 代码页」「并行解压」
  **没有跟着做**：前者引擎本来就会把嵌套归档展平、不是可关的行为；中者需要引擎侧的代码页猜测；
  后者与压缩共用一个进程级线程数，放这里会让人以为有两个旋钮。

### 验证
- `dotnet build Andzip.slnx`：**0 警告 0 错误**。
- `dotnet test`：**304 / 304**（`ExtractionLayoutTests` 补到 20 条：单文件建文件夹用文件名而非包名、
  照样避让重名、开关不影响另两种形状、`RootName` 只在单文件时有值；
  新增 `ExtractionDefaults` 往返与缺省两条）。
- 测试文件从 `CompressionSettingsTests.cs` 改名为 `SettingsDefaultsTests.cs`：
  它现在两页的默认值都管，旧名字会误导。
- 设置页与解压后的行为是 GUI，**按约定由用户手动验证** —— 见 `docs/开发记录.md` 第九节第 25–27 条。

## [0.9.7] - 2026-10-12（新增：压缩级别改 5 档 + 「设置 → 压缩」页）

> 版本号 0.9.6 → **0.9.7**，BuildNo 15 → **16**。

### 新增
- **压缩级别改 5 档**（FR-2.4）：`0-仅存储 / 1-极速压缩 / 2-快速压缩 / 3-标准压缩 / 4-极限压缩`，
  对应引擎 `-mx=` 的 0 / 1 / 3 / 5 / 9。档位按速度排序，因为前三档拼的就是速度、
  标准档看速度与压缩率的平衡、极限档只管压得小 —— 三套不同的评判标准，不是同一算法多跑几遍。
- **设置 → 压缩页**（原来只有占位文案），六项全部真正生效：
  - **默认压缩级别** —— 新建压缩文件时下拉框停在哪一档；右键「压缩为 ZIP / 7Z」也吃它。
  - **压缩单个文件夹时不创建根目录** —— 对应 `IncludeRootFolder` 取反。
  - **排除 Thumbs.db** 与 **压缩时要排除的文件类型**（`*.bak; *.tmp;` 这种写法）。
  - **压缩后测试**、**压缩完成后不关闭进度窗口**。
  - **输出目录**：沿用上次新建时用的目录（压成功后写回 `settings.ini`），或固定到一个目录。
- `Core/Archive/FileNamePattern.cs`：`*` / `?` 的通配匹配，整名匹配、大小写不敏感。
- `Core/Settings/CompressionDefaults.cs`：这一页的默认值集中一处读写，可被测试钉住
  （对话框本身测不了，但它读进来的东西测得了）。
- 语言包新增 16 个键，两侧各 **239 键**、键集与占位符一致。

### 修复
- **`IncludeRootFolder` 的缺省值差点被这一页改掉**：`Load` 里写成
  `GetBool(CompressNoRootFolder, fallback: false)` 会让**没有设置文件的用户**突然多出一层根目录 ——
  而 `ArchiveCreationSettings.IncludeRootFolder` 一直是 `false`。新写的
  `CompressionDefaults_FallbackMatchesTheOldBehaviour` 第一次跑就把它抓出来了，
  缺省改成 `true`（即"不建根目录"）。
- **`CompressionDefaults` 存解析后的列表会让"读回来等于写出去"假失败**：记录级的相等比较对
  `IReadOnlyList<string>` 走引用相等。改成存**原文**（设置页编辑的本来就是那个文本框），
  顺带省掉一次序列化 —— `Save` 写什么，文本框里就显示什么。
- **`CompressionLevelOption.FromId` 不再抛异常**：认不出来的档位号回落到标准档。
  级别号会写进 `settings.ini`，而绿色版可能读到更新版本写下的值；在启动路径上抛一个
  用户看不懂的异常不是容错。与 `ParseDoubleClick` 同一套理由，测试从"未知抛异常"改成"未知回落"。
- **下拉框不再拿 `Id` 当 `SelectedIndex`**：两者今天相等，插入新档位就会分叉。
  新增 `CompressionLevelOption.PositionOf` 并钉住这个区别。

### 变更
- `AppSettings` 补 `GetInt` / `SetInt`（不变文化数字，写盘用 `InvariantCulture`）。
- `ArchiveCreationSettings` 多一个 `ExcludePatterns`；`ArchivePlanBuilder.ShouldExclude`
  在原有 `Thumbs.db` 判断之后按它过滤。
- 排除模式**只认分号不认逗号**：Windows 文件名里逗号合法（`report,final.txt` 是个真名字），
  按逗号切会把一条模式劈成两条不相干的。
- 口令与分卷**刻意不进这一页**：那两项每次都不一样，记住反而危险 ——
  上次压的是加密包，这次压的是要发给别人的公开目录。
- 「压缩完成后不关闭进度窗口」只作用于「新建压缩文件」对话框，**不作用于右键压缩**：
  后者"跑完即消失"是更早明确定下的行为，两者不是一回事。

### 验证
- `dotnet build Andzip.slnx`：**0 警告 0 错误**。
- `dotnet test`：**298 / 298**（新增 `CompressionSettingsTests` 25 条 + 改写级别相关的 5 条；
  `ArchiveCreateZipTests` 的四档往返用例扩到五档）。
- 语言包键集核对：两侧各 **239 键**，无缺失 / 无重复 / `${占位符}` 一致。
- `Andzip.Native.Tests` 与打包见下方流水线结果。
- 设置页与对话框是 GUI，**按约定由用户手动验证**：见 `docs/开发记录.md` 第九节第 20–23 条。

## [构建] - 2026-10-12（`-ExtraCodecs` 首次真编：修掉两个布局 bug，第三个待解）

### 背景
用户要求把压缩级别改成 5 档（仅存储 / 极速 / 快速 / 标准 / 极限），并先拿
`zipcopy/`（NanaZip + 抽出来的编码器）与我们的算法逐个实测比较。核查后的结论有两条：

- **编码器本体没有可比性** —— `zipcopy/Extracted-Compression-Codecs/{LZ4,LZ5,Lizard,Brotli,Zstandard}`
  与我们 `third_party/codecs/` 里的**逐字节相同**（LZ4 1.10.0、zstd 1.5.7，`cmp` 全过）。
  本来就是同一批上游，比出来的只会是编译开关的差别。
- **真正缺的是 7-Zip 胶水层，而那确实是新东西** ——
  `NanaZip.ZS.Codecs/SevenZip/CPP/7zip/{Compress,Archive}` 下的
  `Lz4/Lz5/Lizard/Brotli{Decoder,Encoder,Register}` + 四个 `*Handler.cpp`，
  命名空间是 `NCompress::N<CODEC>`，可以直接落进引擎树。已 vendor 24 个文件。

### 修复
`ANDZIP_EXTRA_CODECS` 从写下来到这次是**第一次真的拿去编译**，一开就暴露三个布局问题：
- **`CMAKE_CURRENT_LIST_DIR` 在函数体里取的是调用方目录。** `codecs.cmake` 里
  `set(CODECS "${CMAKE_CURRENT_LIST_DIR}/../third_party/codecs")` 写在
  `az_add_extra_codecs()` 内部，而该函数是从 `native/CMakeLists.txt` 调用的 ——
  于是解析成 `native/../third_party/...`，落到仓库根上面一层。改成与
  `cmake/7zip-sources.cmake` 里 `AZ_7ZIP_ROOT` 同样的写法：在**文件作用域**算好
  `AZ_CODEC_ROOT` 再给函数用。
- **zstd 要求的 `C/hashes/` 目录我们根本没有。** `zstd_internal.h` 里是
  `#include "../hashes/xxhash.h"`，那是 7-Zip-zstd fork 的布局；xxhash 本体我们已有
  （`third_party/codecs/xxhash/`）。配置期从唯一那份物化出期望路径，代码仍然只有一份。
- **未解：Lizard 的 include 布局。** 它的 `entropy/*.h` 写的是
  `#include "Zstandard/common/mem.h"`，而我们 vendor 的 zstd 是**平铺**在 `C/zstd/*.c`，
  没有 `common/` 这一层。停在这里。

### 变更
- **`codecs.cmake` 不再要求 zstd 的三个胶水文件**，并把原因写在注释里：fork 的
  `ZstdEncoder.cpp` 在 `NCompress::NZstd` 命名空间，只存在于已删除的
  `cankao/7-Zip-zstd-master.zip`；zipcopy 里那份 `NanaZip.Core/Extensions/ZSCodecs/ZstdEncoder.hpp`
  是 `namespace NanaZip::Core::Extensions`，属应用层包装，注册不成引擎 codec。
  **zstd 因此确定仍是只解不编**，不是"再修修就好"。
- `native/README.md`：把「编码需要 `-ExtraCodecs`」这句已经不成立的话改掉，
  「已知限制」里补上三个布局问题的准确状态与 zstd 拿不回来的原因。
- `third_party/MANIFEST.json`：24 个胶水文件的 `origin` 改成如实描述（本地 NanaZip-main
  检出，`cankao/` 已不存在所以 `vendor-sources.ps1 -ExtraCodecs` 无法复跑），
  并新增 `C/hashes/xxhash.h` 一条说明它是配置期物化的。

### 验证
- **默认构建（`-ExtraCodecs` 关闭）完全不受影响**：原生 **532 条断言 0 失败**、
  Core **271 / 271**、`Andzip.slnx` 0 警告 0 错误，暂存的 `andzip_core.dll` 与提交时一致。
  新增文件与 `codecs.cmake` 的改动全部在那个 OFF 开关后面。
- `MANIFEST.json` 仍是合法 JSON（12 条来源）。

> 没有递增版本号：产品代码一行未动，产物与 v0.9.6（Build 14）逐字节相同。
> 递增反而会让两个内容一致的可执行文件顶着不同版本号。

### 待办（用户已提出、本轮未做）
- **压缩级别改 5 档**（仅存储 / 极速 / 快速 / 标准 / 极限），并按
  「1–3 档只比速度、4 档看速度与压缩率平衡、5 档只看压缩率」的标准为每档定默认参数。
- **设置 → 压缩设置页**（现在还是占位文案）。
- 二者的前置分歧：Lizard 的 include 布局修不修得动。即使修不动，5 档也能只靠现有的
  Copy / Deflate / LZMA2 / BZip2 / PPMd 做出来 —— 极速档会弱一些（低级别 Deflate 而非 LZ4）。

## [0.9.6] - 2026-10-12（变更：右键解压改成按包内容判断的智能解压；右键压缩加第三条带对话框）

> 版本号 0.9.5 → **0.9.6**，BuildNo 14 → **15**。

### 变更
- **右键解压 → 智能解压**（`Core/Archive/ExtractionLayout.cs`）：落点不再固定套一层同名文件夹，
  而是读一遍清单看顶层装着什么，分三种情形：
  | 顶层 | 解到哪儿 |
  |---|---|
  | 只有一个文件 | 压缩包所在目录，直接得到那个文件 |
  | 所有内容都在同一个顶层文件夹里 | 压缩包所在目录，整个文件夹放出来 |
  | 多个平级文件或目录混在一起（含空包） | 新建以压缩包命名的文件夹收纳 |

  第二行是这次改动的动机：`docs.zip` 里本来就装着 `docs/…`，固定套层会得到 `docs\docs\`。
  第三种情形新建的目录被占用时依次试 `名称 (2)`、`名称 (3)`…… **文件也算占用**（同名文件一样会让
  建目录失败）；与压缩包的 `UniqueArchivePath` 不同，目录名**不剥扩展名** —— `photos.tar` 撞名要得到
  `photos.tar (2)`，不是 `photos (2).tar`。
- **双击的「直接解压」保持原样**（固定解到同名文件夹）：那是用户更早明确要过的行为，
  不能顺手改掉。所以 `ShellTask.Extract` 与新的 `ShellTask.AutoExtract` 是两个任务，
  右键走后者。`/task=extract` 与裸 `/extract` 仍然认，老注册项不用重做。
- **右键压缩三条**（对齐参考软件）：「压缩为 ZIP」「压缩为 7Z」直接压 + 进度条；
  第三条「压缩到…（可设密码 / 分卷）」把「新建压缩文件」对话框开起来，输入列表与包名预填好，
  点开始才压。口令 / 分卷 / 级别这些需要人看着选的东西静态动词表达不了，所以它是独立一条。
  这条**不自动收尾** —— 窗口关掉才结束进程，用户可能看一眼就取消。
- **ProgID 动词换名**：`shell\extract` → `shell\auto-extract`。`WriteCommands` 里显式删掉旧的
  `shell\extract` 键，否则升级后菜单上会同时留着两项，用户分不清点哪个。
- **新增 `ContextMenuService.RepairVerbs`**：`IsInstalled` 只看"有没有 `Andzip.` 开头的键"，
  所以老用户注册表里只有两条压缩动词时，第三条永远不会自己出现。启动自愈现在会在**已经装了**的
  范围里补齐缺的条目；一个都没装的范围不动 —— 没勾过的用户不该某天发现菜单里多了三项。

### 修复
- **`ShellTasks.IsCompression` 删除**：分发处改成显式 `switch` 之后它就成了只有自己的测试在调的
  死代码，而且"对话框那条算不算压缩"这种问题它回答不了（三条行为各不相同）。
- **语言包清掉 5 个没人引用的键**（`TEXT_SETTINGS_ASSOC_APPLIED` / `_BLOCKED`、
  `TEXT_SETTINGS_CTX_APPLIED` / `_INSTALLED` / `_REMOVED`）：都是前两轮加了没接线的。
  两侧键数从 226 降到 **223**，仍然逐一对齐。
  `LangKeyCoverageTests.DialogKeys` 立刻报了 2 条失败 —— 那份钉住的键清单里也列着其中两个，
  而"扫一遍 `src/` 里的字符串引用"看不到它。这正是那个测试该做的事，所以改的是清单而不是把键加回来：
  顺手把 0.9.5 / 0.9.6 新加的 15 个右键与上下文菜单键也补进 `DialogKeys`，从此它们有守卫了。

### 新增
- 语言包 `TEXT_MENU_AUTO_EXTRACT`、`TEXT_MENU_COMPRESS_DIALOG`、
  `TEXT_SETTINGS_CTX_AUTO_EXTRACT_HINT`（把三种情形写在设置页里，用户不用来翻文档）。
- `NewArchiveWindow(lang, seedPath)`：右键带路径进来时预填输入与包名。包名走
  `ShellTasks.ArchivePathFor` 而不是再拼一遍 —— 文件夹名里的点不能当扩展名剥掉，这条规则已经测过了。

### 验证
- `dotnet build Andzip.slnx`：**0 警告 0 错误**。
- `dotnet test`：**271 / 271**（新增 `ExtractionLayoutTests` 16 条：三种情形的判定、
  空目录包与深层条目推断顶层、分隔符与开头斜杠归一、三种落点、`photos.tar` 撞名保留整名、
  同名文件也算占用、连占两个时递增到 `(3)`）。
- `Andzip.Native.Tests`：**69 条断言 0 失败**。
- 语言包键集核对：两侧各 **223 键**，无缺失 / 无重复 / `${占位符}` 一致，且除语言文件自身用到的
  `LANG_NAME` / `DLG_FONT_*` 外没有未引用键。
- 右键菜单、智能解压的真实落点、对话框预填都属资源管理器与 GUI 交互，**按约定由用户手动验证** ——
  见 `docs/开发记录.md` 第九节第 11–19 条（第 13 条是这次改动要重点确认的那一条）。

## [文档] - 2026-10-12（README 重写为面向开源读者的入口文档）

### 变更
- **`README.md` 重写**。原来的文件已经长成近千行的开发记录：入口位置却放的是架构推导、
  十几个坑的症状与定位过程、逐版本验证表 —— 新来的人想知道「这项目干什么、能做什么、怎么编」
  反而要自己翻。现在按开源读者的阅读顺序重排：**为什么做这个 → 现状（含已完成 / 未完成两张清单）
  → 目录结构 → 架构一眼版 → 怎么构建 → 想贡献 → 许可证与出处**，语言尽量短句、每节能独立读完。
- **原内容整篇保留为 [`docs/开发记录.md`](docs/开发记录.md)**（`git mv`，历史与逐节编号不变），
  开头加了一段说明它的定位：维护者读的深度文档，不是入口。
- 连带修的引用：`CHANGELOG.md` 里 6 处「见 README 第 X 节」重指到 `docs/开发记录.md`（保留
  「当时还叫 README.md」的注记，免得以后对照历史时对不上）；开发记录 §二 目录结构补上新 README，
  §四 与 §九 的 Core 用例数从 235 更正为 **257**（v0.9.5 加了 `ShellTasksTests` 却没回头改表）；
  §十 维护约定新增「文档分工」一条，写清往入口文档塞长篇诊断就是这次的成因。
- 新 README 的每条声明都对过源码或实测：断言数（原生 532 + Core 257 + 外壳 69 = 858）、
  60 种可读 / 7 种可写、语言包 226 键、产物 19.51 MB。「工具 → 修复压缩包 / 密码恢复」
  与「扫描恶意软件」在菜单模型里有名字但没有接线，所以明确列进**未完成**。

> 本节只有文档，没有改一行代码，产物与 v0.9.5（Build 14）完全一致，所以**不单独递增版本号**。

## [0.9.5] - 2026-10-12（新增：右键解压 / 右键压缩；修复：已关联却当不上默认程序）

> 版本号 0.9.4 → **0.9.5**，BuildNo 13 → **14**。

### 修复
- **`.7z` 勾上了、双击却还是 Bandizip**：查注册表看到的是一层以前没有说清的关系 ——
  `HKCU\Software\Classes\.7z` 默认值已经是 `Andzip.7z`，`Andzip.7z` 那棵 ProgID 的
  `shell\open\command`、`DefaultIcon`、`OpenWithProgids` 全部正常，可
  `Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.7z\UserChoice\ProgId`
  写着 `Bandizip.7z`，而 **UserChoice 的优先级更高**。同机器上 `.zip` 的 UserChoice 恰好已经被
  改成 `Andzip.zip`，`.rar` 没有 UserChoice —— 所以只有 `.7z` 表现异常，很容易误判成"关联没写进去"。
  UserChoice 里的 `Hash` 是每用户随机密钥算的，伪造它属于绕过 Windows 10 1709 之后的默认应用保护，
  **不做**。能做的是把这件事说出来并给出路：
  - `FileAssociationService.ShadowedDefaults()` 逐个扩展名比对 UserChoice，
    关联页把这类列成一条红色说明，附「设为默认…」按钮，直接跳到
    `ms-settings:defaultapps?fileExtension=.7z`（Windows 10 不认这个参数时退回默认应用总览页）。
  - 登记过程中新挡下的扩展名仍然当场跳过去，不停留在"只弹一句提示"。

### 新增
- **右键解压**（FR-2 / FR-3 的入口延伸）：每个 `Andzip.<id>` ProgID 里写两个动词 ——
  `shell\extract`（直接解压到「包名」文件夹）与 `shell\extract-here`（解压到当前文件夹），
  命令行是 `Andzip.exe "%1" /task=extract[-here]`。跑起来**只有百分比进度条，完成即消失**，
  不创建主窗口。
- **右键压缩**（FR-2 的入口延伸）：`HKCU\Software\Classes\*\shell` 与 `…\Directory\shell` 下各写
  `Andzip.compress-zip` / `Andzip.compress-7z` 两个静态动词，同样只弹进度条。输出取源路径的同名
  兄弟位置，**重名往后加序号**（`report (2).zip`）而不是覆盖 —— 右键是用户随手点的，静默删掉一个
  已有的包不可原谅。压文件夹时把文件夹本身装进去（`docs.zip` 里是 `docs/…`）。
- **`Core/Archive/ShellTasks.cs`**：任务枚举、命令行写法（`/task=…`，兼容 v0.9.4 的裸 `/extract`）、
  解压落点与压缩输出名规则集中一处。纯逻辑，没有注册表也没有 GUI 就能测。
- **设置 → 上下文菜单页**（原来只有占位文案）：三个开关 —— 为所有文件加「压缩为」、
  为文件夹加「压缩为」、为已关联的压缩包加「解压」。撤销干净：只删带 `Andzip.` 前缀的子树。
- **进度条窗口**现在压缩与解压共用，「常规 → 解压过程中允许按 Esc 取消」同时管着两边。
- 语言包新增 12 个键（右键文案、上下文菜单页文案、「设为默认…」按钮等），
  English / SimpChinese 各 **257 行、226 个键，键集与占位符一致**。

### 变更
- **动词不再承载行为**：`shell\open\command` 永远不带开关（双击做什么仍由「常规 → 双击」决定），
  右键动词一律带 `/task=`。`SettingKeys.ExtractSwitch` 与 `DirectExtractDestination` 删除，
  规则收进 `ShellTasks`；`ShouldExtractDirectly` 的 `hasExplicitSwitch` 参数一并去掉 ——
  显式开关现在由 `ShellTasks.TryParse` 表达，留两个入口迟早分叉。
- **启动分发**：`App` 改成「解析出任务 → 交给 `ShellTaskRunner`」，`/task=open` 与没有开关时
  照旧建主窗口。命令行里的目标路径现在也接受**目录**（右键压缩文件夹要用 `%1` 传目录，
  而 `File.Exists` 对目录返回 false，会把整个入口漏掉）。
- **`RepairCommands` 会补齐三个动词**：升级后通过关联被拉起来时，把自己命名空间下的 ProgID
  整体重写一遍（命令行指向当前 exe、动词按「上下文菜单」的选择补齐），老关联不用重做就能有右键菜单。
- **`ContextMenuService` 拆成两个范围**：`Install(verbs, exe, forFiles, forFolders)` / `Remove()` /
  `IsInstalled(scope)`。设置页的两个「压缩」开关状态读自注册表 —— 它们是装好就固定的全局动词，
  被清理软件删掉了界面就该显示没装；「解压」开关存的是**策略**（`context_menu_extract_verbs`，
  缺省为开），因为 ProgID 会随新关联不断增多，每次登记与每次自愈都按它补写。
- 文档：`docs/开发记录.md`（当时还叫 `README.md`）第五节改写「设置与文件关联」，新增后台任务与上下文菜单页两段；
  第六节新增第 21（UserChoice 优先级实测）、22（静态动词的三条限制）、23（右键压缩走固定默认档位）条。

### 已知限制（本版）
- Windows 11 上静态动词落在**「显示更多选项」二级菜单**里；进新菜单第一层要写 `IExplorerCommand`
  的 in-proc shell 扩展，本版不做。
- 静态动词只能用 `%1`，**一次一个文件或一个文件夹**；多选请进主窗口用「新建压缩文件」。
- 菜单文字是死的，渲染不出参考软件那种「压缩为 "文件名.zip"」。
- 右键压缩走固定默认档位：正常压缩、无口令、不分卷、不测试、压完不删源。

### 验证
- `dotnet build Andzip.slnx`：**0 警告 0 错误**。
- `dotnet test`（Release）：**257 / 257**（新增 `ShellTasksTests` 25 条：动词往返、兼容裸
  `/extract`、无开关时不越权、两种解压落点、`archive.tar.gz → archive.tar`、目录名带点不剥
  （`my.app` → `my.app.7z`）、重名加序号且不动已有包、非解压任务拒绝算落点）。
- `Andzip.Native.Tests`：**69 条断言 0 失败**。
- 语言包键集核对：English 与 SimpChinese 各 226 键，**无缺失、无重复、`${占位符}` 一致**；
  清掉了上一轮 Python heredoc 遗留的 16 处 `\"` 转义污染。
- `build.ps1` 全流水线通过，产物 `dist\Andzip-v0.9.5-win-x64.exe` **19.51 MB**，
  二进制内确认已内嵌 `andzip_core.dll`。
- 右键解压 / 右键压缩 / 「设为默认…」跳转均属 GUI 与资源管理器交互，**按约定由用户手动验证** ——
  见 `docs/开发记录.md` 第九节清单第 11–15 条。

## [0.9.4] - 2026-10-12（修复：设成「直接解压」后双击仍然弹主窗口）

> 版本号 0.9.3 → **0.9.4**，BuildNo 13。

### 修复
- **双击行为绑在注册表动词上，留下半截状态**：用户实测「常规 → 直接解压」设完仍弹主窗口。
  查注册表看到的就是症状本身 —— `Andzip.zip\shell` 的默认值被改成了 `extract`，但
  `shell\extract\command` **根本不存在**（那棵 ProgID 是 v0.9.2 注册的，那时还没有 extract 动词），
  Windows 找不到动词就退回 `open`，于是打开主窗口。
  现在**行为改由程序启动时读设置决定**（`SettingKeys.ShouldExtractDirectly`）：注册表只负责
  「双击把文件交给我们」，怎么处置是设置说了算。默认动词恒为 `open`，改设置对已关联的
  所有扩展名立即生效，也不再依赖某个动词键是否存在。
- **注册表里指着旧 exe**：同一台机器上那条命令写的是 `dist\Andzip-v0.9.2-win-x64.exe` ——
  绿色版文件名带版本号，升级后旧路径仍然有效，而那个老二进制不认新行为。现在通过关联被拉起来时
  会把自己命名空间下的 `Andzip.*` ProgID 命令行校正到当前运行的 exe（`FileAssociationService.RepairCommands`），
  顺带补齐缺失的动词键。只动 `Andzip.*`，不碰别的软件登记的 ProgID。

### 验证
- `dotnet build Andzip.slnx` 0 警告 0 错误；`dotnet test` **235 / 235**（新增 6 条
  `ShouldExtractDirectly` 判定用例）；`Andzip.Native.Tests` 69 条断言 0 失败。
- **完全模拟双击**（只传文件路径、不带任何开关）：设置 = `extract` 时进程自己以 0 退出、
  文件解到 `dl\` 下、不出现主窗口；设置改回 `open` 时同样的调用方式弹出主窗口并显示该包。
- **注册表自修复实测**：修复前 `shell` 默认值是 `extract` 且没有 `shell\extract` 子键、命令指向
  v0.9.2；跑过一次之后 —— 默认动词回到 `open`，`shell\extract\command` 存在，命令路径指向当前 exe，
  `.zip` 默认值为 `Andzip.zip`。
- 用户机器上原有的 `file_association_defaults` 记的是 `Bandizip.zip` / `Bandizip.7z` 等，
  说明「接管前记下原默认程序」这条路径本身是好的，撤销时能把默认值还给 Bandizip。
- 真机双击（资源管理器里）仍待用户确认 —— 见 `docs/开发记录.md` 第九节第 9 条。

## [文档] - 2026-10-12（体积构成实测记录，无代码改动）

### 变更
- **文档**：把「20 MB 到底花在哪」量清楚写进 `docs/开发记录.md`（当时还叫 `README.md`）第四节第 5 条 —— 未打包 41.39 MB 里
  渲染栈占 15.87 MB、.NET 运行时约 12 MB，而 **`andzip_core.dll` 只有 1.67 MB（4%）**，
  与「7-Zip 本体不到 2 MB」同量级。结论：体积与静态/动态链接库的形式无关，C++ 产物必须是机器码
  二进制，换形式不会让代码消失；NativeAOT 反而更大。同时记下三条真能压的路（去 ANGLE ≈17 MB /
  框架依赖 zip ≈12 MB / 换 UI 技术栈 2–3 MB）与各自的代价，以及本版的决定：
  **接受 19.5 MB，继续做功能**，在放弃「免安装 / 不挑机器 / GPU 渲染」三者之一以前不再优化体积。

> 本节只有文档，没有改代码，产物与 v0.9.3 完全一致，所以**不单独递增版本号**。

## [0.9.3] - 2026-10-12（FR-5 常规设置页：双击行为与 Esc 取消）

> 版本号 0.9.2 → **0.9.3**，BuildNo 11 → 12。

### 新增
- **常规设置页**（照参考软件的样式：小标题 + 左标签右控件），两项：
  - **双击压缩包文件时** → `打开 Andzip` / `直接解压`。这不是界面偏好开关：它改写注册表里所有
    `Andzip.*` ProgID 的默认动词（`shell` 键的默认值），所以在设置里改完，资源管理器里双击才真的换行为。
    关联时就把 `shell\open\command` 与 `shell\extract\command` 两个动词都注册好，切换只改一个值 ——
    不删不重建 ProgID，也就不会把用户在该 ProgID 上的其它改动一起带走。
  - **解压过程中允许按 Esc 取消**（默认允许）。关掉之后进度窗口不吃 Esc，防止大文件解到一半
    一次误触前功尽弃；「取消」按钮始终可用。
- **`/extract` 直接解压入口**（`src/Andzip.App/DirectExtract.cs`）：`Andzip.exe <包路径> /extract`。
  不创建主窗口，只弹进度窗口，跑完或取消后自动关闭并结束进程；生命周期切 `OnExplicitShutdown`，
  否则默认策略下没有主窗口会让进程刚起来就退出。落盘位置固定为压缩包所在目录下一个与压缩包
  同名的文件夹，与「选择解压路径」里默认勾选的那一项一致，不另起第二套规则。
  加密包先问口令再开工，避免解到一半才报“要口令”留下一堆半截文件。
- `Andzip.Core/Settings/SettingKeys.cs`：键名集中一处（免得各处拼字符串拼歪），双击取值存稳定的
  小写标识而不是数字（以后插新值不会让老设置错位），认不出的取值一律退回「打开 Andzip」。

### 变更
- **默认程序没改成时，直接跳到系统设置**：以前只弹一句“被 Windows 默认应用保护挡住”的提示，
  现在直接开 `ms-settings:defaultapps?fileExtension=<扩展名>`（Windows 10 不认这个参数，
  失败就退回 `ms-settings:defaultapps` 总览页）。用户不必自己去翻设置。
- 设置窗口的「重置 / 确定」改成按当前页分派（页面实现 `Reload` / `Commit`），不再只服务关联页。
- 默认打开的是「常规」页，与参考软件一致（以前直接落在文件关联页）。

### 验证
- `dotnet build Andzip.slnx` 0 警告 0 错误；`dotnet test` **229 / 229**（新增 12 条：
  双击取值编解码与回落 6 条 + 直接解压落盘位置 3 条 + 其余 3 条）；`Andzip.Native.Tests` 69 条断言 0 失败。
- `/extract` 这条命令行路径**做了端到端验证**（不是 GUI 操作）：用临时造的 zip 跑
  `Andzip.exe payload.zip /extract`，进程 25 秒内自己以 0 退出、任务管理器无残留，文件落在
  `payload\a.txt` 与 `payload\sub\b.txt`（同名目录 + 保留子目录结构）。同时回归了普通打开
  （`Andzip.exe payload.zip` → 标题变包名）与无参启动（标题带内核信息）两条路径。
- **注册表动词切换与 Esc 开关仍需手动验证**：双击行为、ProgID 默认动词改写、以及被
  `UserChoice` 挡住时跳转系统设置，都在 `docs/开发记录.md` 第九节第 6 / 7 / 9 / 10 条列了步骤。

## [0.9.2] - 2026-10-12（修复：文件关联点了没生效）

> 版本号 0.9.1 → **0.9.2**，BuildNo 10 → 11。修的是用户实测反馈：勾了 ZIP 并应用之后，
> 双击 .zip 仍然由系统默认工具打开。

### 修复
- **只挂「打开方式」，没设默认程序**：上一版的关联刻意只写扩展名的 `OpenWithProgids`
  （不想抢系统默认程序），结果双击当然还是走原来的默认程序 —— 但对压缩工具来说，
  “双击就解压”就是这个功能的本意。现在两处都写：`OpenWithProgids` 让右键列表里有我们，
  扩展名键的默认值让**双击**走我们。
- **关联页显示的是愿望而不是现实**：勾选状态原先取自 `settings.ini`，那是“上次写成功的那批”。
  实测这台机器上设置文件记着 20 项已关联，注册表里却只剩 `Andzip.chm` 一棵 ProgID、
  `.zip` 的 `OpenWithProgids` 里根本没有我们 —— 页面一片勾，系统里没生效，用户再点「应用」
  也看不出自己点过了（因为按设置文件的记录“没变化”）。现在打开页面逐项问注册表
  （`FileAssociationService.IsRegistered`），「立即应用」的可用性也跟着真实状态走。
  「重置」的语义随之改成“重新读系统状态、放弃未应用改动”。

### 变更
- **接管默认程序前先记下原值**，取消勾选时按记录还原（当时没有默认值就删掉那个值），
  存在 `settings.ini` 的 `file_association_defaults` 里。只还原“现在还指着我们”的扩展名，
  期间被别的软件改过默认程序就不动它。`FileAssociationCatalog` 的 `DefaultSelected` /
  `DefaultIds` 一并删掉：既然列表以注册表为准，“预勾选”就是撒谎的来源。
- **被 `UserChoice` 保护的扩展名如实报错**：Windows 10 1709 之后默认应用带哈希保护，
  伪造那个哈希属于绕过系统安全机制，不做。这类扩展名应用后会弹提示列出是哪几个，
  让用户去「设置 → 应用 → 默认应用」手选，而不是静默失败。实测 .zip / .7z / .rar /
  .tar / .gz 都没有 UserChoice，写 `HKCU\Software\Classes` 即生效。

### 新增
- `Andzip.Core/Settings/AssociationOriginals.cs`：接管前默认程序的 `扩展名=ProgID` 编解码
  （只按第一个 `=` 切分，脏记录跳过而不是当成“把默认值设成空”的指令）。9 条单元测试。

### 验证
- `dotnet build Andzip.slnx` 0 警告 0 错误；`dotnet test` **217 / 217**；
  `Andzip.Native.Tests` 69 条断言 0 失败。
- 注册表现状是**只读排查**出来的（`reg query` + PowerShell 读键），确认了“设置文件与注册表
  不一致”这个诊断，也确认这些扩展名没有 UserChoice。
- **双击行为与注册表写入仍未做自动化验证**（GUI 由用户手动测）。手动清单在 `docs/开发记录.md` 第九节
  第 6 / 7 / 8 条：勾 ZIP → 应用 → 双击 .zip 应当用 Andzip 打开；取消 → 应用 → 双击应当回到
  原默认程序；重开设置勾着的项目应当仍然勾着。

## [0.9.1] - 2026-10-11（FR-5 设置窗口第一片：文件关联）

> 版本号从 0.9.0 / Build 9 递增到 **0.9.1 / Build 10**。上面两节「未发布」的内容与本节一起构成
> 这个版本的变更集（此前它们没有单独发版，`VersionNo.ini` 一直停在 0.9.0）。

### 新增
- **设置窗口（FR-5）**：`选项 → 设置（F5）`，`Views/SettingsWindow.cs`。左侧蓝色导航 + 右侧页面 +
  底部「重置 / 确定」，导航顺序照参考软件（常规 / 文件关联 / 上下文菜单 / 解压 / 压缩 / 查看 /
  安全 / 高级 / 语言）。**本片只实装「文件关联」**，其余 8 页显示占位文案而不是假装能配。
- **文件关联候选目录**：`Andzip.Core/Archive/FileAssociationCatalog.cs`，35 项分四组
  （压缩包文件 / UNIX / 其它 / CD-DVD）。候选集由 `Supported(引擎可读扩展名)` 过滤，
  所以列表跟着引擎能力走：哪天引擎读不了某个格式，那一行自己消失。目录刻意排除
  APFS / NTFS / ELF / PE(dll,sys) / VHD 这类「嗅探得出来但不是压缩包」的格式，以及
  Compound（扩展名是 doc / xls / ppt）—— 它们仍然能打开，打开对话框与命令行走内容嗅探。
- **Windows 文件关联登记**：`Services/FileAssociationService.cs`。写
  `HKCU\Software\Classes\Andzip.<id>`（ProgID + DefaultIcon + `shell\open\command`），
  并把 ProgID 挂进该扩展名的 `OpenWithProgids`。**不改扩展名键的默认值**，因此只让 Andzip
  出现在右键「打开方式」里，不抢系统默认程序；取消勾选整棵删除，撤销干净。改完
  `SHChangeNotify(ASSOC_CHANGED)` 让资源管理器立刻刷新。全部走 HKCU，不需要管理员。
- **应用设置存储**：`Andzip.Core/Settings/AppSettings.cs`，扁平 `key = value` 文本，落在
  `%APPDATA%\Andzip\settings.ini`。没用 JSON：反射式序列化在 `PublishTrimmed` 下要额外保留配置，
  为一堆标量不值当，而且语言包与 VersionNo.ini 本来就是这套语法。跟着用户走而不是程序目录，
  因为绿色版常被放在只读位置。
- 语言包新增 24 个键（设置窗口导航、按钮、分组标题与提示），两种语言同步并进 `LangKeyCoverageTests`。

### 变更
- 「确定」= 有未应用改动就顺手应用再关窗，右上角 X 才是放弃改动；「重置」恢复**出厂默认勾选**
  （ZIP / 7Z / RAR / TAR / GZ… 勾上，EXE / IMG / JAR 等不勾），不是放弃未保存改动。
- 「立即应用」只在勾选与已保存状态不一致时可用（`Enabled` 而不是点了没反应）。
- 参考软件关联页上的「基本选项」按钮没有跟着做：它的行为无法从截图确定，与其放一个不知道会不会
  有用的按钮不如先不放。

### 验证
- `dotnet build Andzip.slnx` 0 警告 0 错误；`dotnet test` **209 / 209**（新增 22 条：
  关联目录 9 条 + 设置存储 13 条）；`Andzip.Native.Tests` 69 条断言 0 失败。
- 关联目录里有一条钉子用例：`EveryCatalogExtension_IsActuallyReadableByTheEngine` ——
  目录写的每个扩展名都必须在引擎实际报告的可读集合里，防止将来加一行勾了却打不开的关联。
- **界面与注册表写入未做自动化验证**（按约定 GUI 由用户手动测）。手动清单见 `docs/开发记录.md` 第九节
  第 6 / 7 条：勾 LZMA → 立即应用 → 右键「打开方式」应出现 Andzip；取消 → 应消失；
  重置 / 确定 / 重开 F5 验证持久化。

## [未发布] - 2026-10-11（FR-1 首页 + FR-4 压缩包浏览视图）

> 版本号仍是 0.9.0 / Build 9。本条只动界面与 Core 的呈现层，未触碰 `native/` 内核。

### 新增
- **压缩包目录树（FR-4）**：`src/Andzip.Core/Archive/ArchiveHierarchy.cs` 把扁平清单重建成
  目录节点树。必要性不在「画一棵树」，而在**很多 ZIP 只写文件路径、不写目录条目** —— 照抄
  `is_dir` 为真的那些行会漏掉整层目录。路径按 `/` 切段并丢掉空段，所以 `a//b`、`/a/b`、
  `a/` 不会在树上变成三个节点；目录行优先用清单里的真实条目（带回时间 / CRC / 属性），
  没有才合成。7 条单元测试覆盖这些规则。
- **按清单序号回读条目内容**：`ArchiveContentSession`（Core）为图标视图的缩略图服务。
  它只开一个归档句柄并用一把锁串行化 —— 引擎的句柄不是线程安全的，而每张图都重开一次
  归档会把 400 次中央目录解析变成实际等待。`ReadEntry(index, maxBytes)` 在超过上限时返回
  null 而不是先把 500 MB 塞进 LOH。
- **压缩包视图（FR-4）**：`Views/ArchiveView.cs` 从 4 列平面列表换成「左树 + 右 11 列详情」，
  列与参考软件逐字对齐（名称 / 压缩后大小 / 原始大小 / 类型 / 修改日期 / 压缩方法 / 加密算法 /
  循环冗余检验 (CRC) / 属性 / 注释 / 附加信息）。表头点击排序（目录在前、文件在后，各自按
  名称码位序），`..` 恒在首行；表头放在一个只横向滚动的 ScrollViewer 里并与列表的横向偏移
  同步，这样列超宽时能横滚而纵向仍走 ListBox 虚拟化。
- **图标视图**：图像条目用 `Bitmap.DecodeToWidth` 缩到 128px 生成缩略图，后台读、UI 线程贴，
  切目录时取消上一批请求；条目超过 400 个或单条超过 32 MB 就退回通用图标。
- **主工具栏（FR-1）**：`Views/MainToolBar.cs`，蓝色带上的图标 + 文字大按钮，「打开 / 解压」
  带下拉。已接命令的三个（打开 / 解压 / 新建）真能点，其余（添加 / 删除 / 测试 / 扫描 / 语言）
  是禁用态而不是假装有响应。
- **底部栏三个按钮**：详情列表 / 图标视图 / 在系统文件管理器里定位压缩包（`explorer /select`、
  `open -R`、`xdg-open`，路径走 `ArgumentList` 而不是拼字符串）。
- **打开时问口令**：清单本身在包里，所以「先试一次、引擎要口令再弹框」，而不是每次都先问一遍。
- **命令行传入压缩包路径直接打开**（`App.cs`）：文件关联的入口，也是无扩展名包的兜底路径。
- 语言包新增 21 个键（工具栏文案、列名、附加信息），两种语言同步，全部进 `LangKeyCoverageTests`。

### 变更
- **首页从「两张卡片的启动页」换成与压缩包视图同构的空白双栏**（`Views/HomeView.cs`，
  删除 `Views/StartupScreen.cs`）：打开与关闭压缩包时窗口布局不跳，「未打开」与「已打开」
  是同一个视图的两个状态而不是两个界面。随之删除不再使用的 `TEXT_HELP_PAGE`、
  `TEXT_STARTUP_OPEN`、`TEXT_STARTUP_NEW`（底部「帮助页面」栏一并去掉）。
- **「打开」对话框只能选受支持的格式**：删掉「所有文件」这一项（`TEXT_ALL_FILES` 随之移除），
  并把过滤器从「每种格式的主扩展名」改成**全部**扩展名（58 → 115 个）。原来 `.Take(1)` 会
  把 `.tgz` / `.txz` / `.apk` 这类只出现在副位置的扩展名整个丢掉 —— 我们自己能创建 `.tgz`，
  却在打开框里选不了它。
- **属性列在格式未存属性时补默认位**（目录补 `D`、文件补 `A`），显示成 `D__` / `A__` 而不是
  整列空白；只读与隐藏位仍只报真实数据。
- **状态栏的「压缩文件大小」改成包体物理大小**（`ArchiveListing.ArchiveSize`，取自引擎的
  `az_archive_physical_size`），标签字面意思与显示值对齐；实测 965.7 MB 与参考软件一致。
- **菜单与文件框标题去掉 Win32 助记符标记**（`MnemonicText.cs`）：SimpleTheme 不解析 `&`，
  不过滤就显示成「文件(&F)」。语言包保留原写法，将来接真正的访问键时不用回头改文案。

- **打包体积 19.72 → 19.47 MB**：`build.ps1` 的发布命令补 `DebuggerSupport=false`、
  `EventSourceSupport=false`、`MetadataTrimming=complete`。同时记下三条**试过但不要再用**的：
  框架依赖发布反而更大（25.18 MB，不能裁剪且原生库压不动）、`UseSystemResourceKeys` 零收益
  还会把异常文案换成资源键（我们的错误框直接显示 `ex.Message`）、手工摘调试用原生库只省 30 字节
  （`mscordbi` / `DiaSymReader` 本来就不进 bundle）。构成见 `docs/开发记录.md`（当时还叫 `README.md`）第四节第 5 条。

### 修复
- **详情列表长文字压到隔壁列、列宽不可调**：两个独立的原因。
  ① 名称列原本是「图标 + 文字」的 `StackPanel`，而 StackPanel 测量子项时给的是**无限宽度**，
  `TextTrimming` 因此永远不触发，长文件名直接盖住压缩后大小 / 原始大小两列 —— 换成
  `Auto,*` 的两列 Grid 后文字才被约束住。
  ② 行 `Grid` 上的 `ClipToBounds` 把超宽内容就地裁掉了，ListBox 于是认为「没有横向溢出」，
  既不出横向滚动条也滚不动，右边几列等于永远够不到。去掉行裁剪并显式
  `ScrollViewer.SetHorizontalScrollBarVisibility(list, Auto)` 后滚动条回来了。
  表头原先包在一个 `HorizontalScrollBarVisibility=Hidden` 的 ScrollViewer 里，它和列分隔条的
  拖拽会抢按下事件；改成「表头平移到 `-Offset.X` + 外层 Border 裁剪」，嵌套 ScrollViewer 一并去掉。
  列分隔条用 `GridSplitter`（`ResizeDirection=Columns`、`ResizeBehavior=PreviousAndNext`，
  每列带 `MinWidth=48`），松手时才把宽度落到实例字段并重画列表 —— 每帧重建会让拖动本身卡顿。
  顺带给首页与压缩包视图的树栏分隔条补上同样的 `ResizeDirection`，之前依赖 Auto 推断。
- **底部栏按钮的图标不显示**：`Avalonia.Controls.Shapes.Path` 在代码里构造时没有隐式的
  `StrokeThickness`，只给 `Stroke` 不给粗细就什么都不画。截图核对时才发现 —— 按钮的
  高亮底色在，图标是空的。

- **属性列对 Python 写的 ZIP 显示 `___`**：ZIP 外部属性字段的高 16 位是 Unix mode
  （`0o644 << 16` = `0x81A00000`），而 Win32 属性只占低 16 位；整字去测 D/A 位会一个都测不到。
  改成先 `& 0xFFFF` 再判位，低 16 位为 0 时才回落到默认位。

### 验证
- `dotnet build Andzip.slnx` 0 警告 0 错误；`dotnet test` **187 / 187**（新增 9 条：
  目录树 8 条 + 内容回读 1 条）；`Andzip.Native.Tests` 69 条断言 0 失败（含 4000 次写入压力）。
- 列宽修复后的复验：默认窗口尺寸（1040×773）下横向滚动条出现；点滚动条轨道翻到右侧后，
  表头与内容逐列对齐（CRC / 属性 / 附加信息 三列文字一一对上），长文件名显示为省略号且
  不再与相邻列重叠。**拖拽列宽本身没有被自动化验证到**：本机的合成鼠标输入只能产生点击，
  压住再移动不会被 Avalonia 当成拖拽（连拖动滚动条滑块都没有反应），所以只验证到
  「分隔条配置正确 + 表头不再包在 ScrollViewer 里」这一层。
- **界面逐屏截图核对**（一个 402 文件 / 4 目录 / 965 MB 的真实照片包）：首页、打开后的树与
  11 列详情、进入子目录（`..` + `JPG 文件` / `Deflate` / CRC / `A__` / `OS:Windows`）、
  图标视图（100 张证件照缩略图全部解出）、系统文件框（标题「打开压缩文件」，类型下拉只有
  压缩包一项）—— 五屏逐条对照参考截图。

## [未发布] - 2026-10-11（重构后的交付目录 + 内核修复）

> 版本号仍是 `VersionNo.ini` 里的 0.9.0 / Build 9；下一版正式发布时再按 §11.2 递增。

### 新增
- **CI**：`ci.ps1` 是门禁本体（引擎 → 导出核对 → 静态库 → 解决方案构建 → 两套托管测试 →
  打包），`.github/workflows/ci.yml` 的 Windows 作业直接调 `ci.ps1 -Static`，本地与 CI 跑同一份；
  另加 `linux`（真机执行 `andzip_core_tests` + 导出核对）与 `macos`（探测）两个作业。Linux 的
  托管测试刻意 `continue-on-error`，因为它从没在非 Windows 上跑过。
- **`.gitignore`**：排除 `build/`、`dist/`、`bin/`、`obj/`、`.vs/` 等约 740 MB 可再生内容，
  只保留已暂存的内核 DLL 入库（否则托管作业没有引擎可加载）。
- **`build-unix.sh` 把内核暂存到 `src/Andzip.Native/runtimes/<rid>/native/`**，与
  `build-windows.ps1` 同一套约定；此前它只在构建目录旁放一份，头部注释的说法与事实不符。
- **`run.ps1`：不依赖 Visual Studio 的运行入口**。`run.ps1` 构建并启动主程序，
  `run.ps1 -Check` 构建并跑两套测试。它会先确认 `andzip_core.dll` 已暂存在外壳工程里，缺了就
  报错并给出重建命令，而不是让程序起来后显示「压缩引擎不可用」。（VS 里 F5 依赖每个用户的
  `.suo` 启动工程状态，`.slnx` 记录不了它 —— 这条路径把这个不确定性绕开了。）
- **`native/scripts/verify-all-targets.ps1`**：一条命令跑完 Windows 动态 + Windows 静态 +
  Linux 三个交叉目标并核对导出符号。
- **`native/` 成为 App 的实际压缩路径（FR-1～FR-3）**：`src/Andzip.Core/Archive/NativeEngine.cs`
  是唯一的接缝，格式表 / 方法表 / 可写格式全部改成运行时向内核查询；`Andzip.slnx` 的五工程
  （App / Core / Native / 两套测试）自包含在 `Andzip/` 目录内 —— **构建、测试、运行、打包
  都不需要目录外的任何文件**（引擎以已暂存的 `andzip_core.dll` 形式随目录走，连 CMake 和
  C++ 工作负载都不用）。只有「重新 vendor 第三方源码」这一步需要上游压缩包，见
  `vendor-sources.ps1 -SourceArchives`。
- **`build.ps1` 测试门禁补跑 `Andzip.Native.Tests`**（69 条断言的控制台自检程序）。它是 xUnit-free
  的 `OutputType=Exe`，`dotnet test` 对它只还原不运行，门禁此前静默漏掉了整层 ABI 检查。
- **常驻压力检查 `writer stress`**：在 CLR 宿主里连续做 4000 次「加密 ZIP 建包 → 加成员 →
  finish → 释放 → 读回」。针对的就是下面那条未初始化 `PROPVARIANT`——它只在宿主的堆/栈页被
  复用过时才暴露，普通单测抓不到。反向验证：把修复删掉后 6 次运行崩 5 次；恢复后 6 次全绿。

### 修复
- **内核：未初始化的 `PROPVARIANT` 被当作「已分配」释放，导致间歇性 0xC0000005**（FR-2 / FR-3，
  致命）。`native/src/api` 里 8 处局部变量与 3 个属性辅助函数用 `PropVariantClear` 代替
  `PropVariantInit` 做初始化，而 `PropVariantClear` 会**读** `vt` 并对垃圾指针执行
  `SysFreeString` / `CoTaskMemFree` / `Release`；`CObjectVector::AddNew()` 的 `new T` 不清零 POD
  成员，所以 `PropEntry::Value` 也是未初始化的。表现为测试主机每隔几次崩一次、用例数从 178
  掉到 169/172，栈顶固定在 `az_writer_finish`，且 ZIP 明显比 7z 容易触发。定位与验证过程见
  `docs/开发记录.md` 第七节第 10 条。POSIX 侧在 `az_pch.h` 补了 `PropVariantInit` 内联垫片。
- **外壳：`ArchiveWriter` / `ArchiveReader` 的选项块 use-after-free**。`NativeArgs`（含进度回调
  的委托与 `az_callbacks`）此前挂在「打开」方法上，方法返回即释放，而内核在 `Finish()` /
  `ExtractTo()` / `Test()` 期间还要回调它。改为把所有权交给句柄对象，`Dispose()` 里在
  `az_writer_free` / `az_archive_close` **之后**才释放。
- **`az_writer.cpp` 的两段中文注释触发 `C4819`**：内核工程未加 `/utf-8`，编译器按本地代码页
  936 读 UTF-8 源码。已翻成英文，`native/src`、`native/include` 现在纯 ASCII，构建 0 警告。
- **「新建压缩文件」对话框把线程数写进进程级设置后不还原**（FR-2.3）：`ArchiveEngine.NumThreads`
  是引擎的全局设置，对话框在启动压缩前直接改它，之后所有压缩/解压都沿用这个值。现在记下原值并在
  `finally` 里还原。
- **外壳自检里一条恒真的断言**：`Check("only files under dest were written", allFiles.Count >= 0)`
  永远成立，等于没断言；改成先断言「确实写出了文件」（`Count > 0`），再逐个断言落在目标目录内。
- **`vendor-sources.ps1` 在交付目录里指向不存在的 `Andzip/cankao`**：重新 vendor 会直接失败。
  现在探测 `Andzip/cankao` 与上一级 `cankao/`，都找不到就报错并提示新的 `-SourceArchives` 参数。
- **`NativeArgs` 的生命周期注释与实际契约相反**：注释写「活到收到它的那次原生调用返回为止」，
  而 `az_writer_open` / `az_archive_open` 会把回调指针存进句柄、到 finish/extract 才用 —— 正是
  这句注释引出了前面那个 use-after-free。注释改成区分「一次性调用」与「产出句柄的调用」两种寿命。
- **`build.ps1` 门禁注释里的断言数（68）与实际（69）不符**。
- **Linux / macOS 上内核永远加载不到**：`CMakeLists.txt` 无条件 `PREFIX ""`，产物叫
  `andzip_core.so`，而外壳探测的是 `libandzip_core.so`（macOS 同理）。Windows 不受影响，交叉
  编译也照样"通过"，所以一直没暴露。现在 `PREFIX ""` 只在 `WIN32` 下设置，三个交叉目标重新
  构建核对：`libandzip_core.so` ×2 各 41 导出，`libandzip_core.a` 30.9 MB 链接进 `azcore`。
- **门禁脚本会假绿**：`verify-all-targets.ps1` 只检查 `$LASTEXITCODE`，而 `cmake` 不在 PATH 时
  抛的是 `CommandNotFoundException`、退出码不变，于是 Linux 三个目标整段没跑仍报成功。两个门禁
  脚本现在都 `try/catch` + 带标签抛出，并由脚本自己用 vswhere 定位 VS 自带的 CMake/Ninja。
- **`ci.ps1` 里 `Platform=X64` 泄漏**：同进程调用 `build-windows.ps1` 会把 vcvars 环境带进来，
  MSBuild 把它当成解决方案平台，后续 `dotnet build` 报 `MSB4126 "Release|x64" is invalid`。改为
  子进程调用，并在脚本开头清掉继承的 `Platform`（解决方案只有 Any CPU）。

### 变更
- **C# 外壳工程从 `native/csharp/` 移到 `src/` 下**（`src/Andzip.Native`、
  `src/Andzip.Native.Tests`）。原因：Visual Studio 在解决方案没有 `.suo` 状态时按**项目路径
  字典序**选默认启动项目，`native/csharp/Andzip.Native` 排在 `src/Andzip.App/...` 之前，于是
  启动项目被占成类库，双击 `Andzip.slnx` 后按 F5 报「无法直接启动带有『类库输出类型』的项目」。
  移动后字典序第一项就是主程序工程，F5 直接可跑。连带更新 `Andzip.slnx`、`Andzip.Core.csproj`
  的 `ProjectReference`、`build.ps1`、`run.ps1`、`build-windows.ps1` 的 DLL 暂存路径，并删除
  交付目录里的 `.vs/`（其中残留的 `.suo` 仍记着旧的启动项目）。`native/` 仍是完整的 C/C++ 内核。
- **文档：第四节补「怎么运行」**：VS 的 F5 行为与本地 `.vs` 状态的关系、以及一张表区分主程序
  `dist/Andzip-*.exe`、开发版 `Andzip.exe`、内核验证界面 `azgui.exe`（**不是主程序**）与命令行
  `azcore.exe`。

### 测试结果
- 原生 532 条断言：动态库、静态库（`/WHOLEARCHIVE`）各一遍，0 失败。
- `dotnet test Andzip.slnx`：178 / 178 通过，**连续 5 次运行稳定**（此前每次数量都不同）。
- 控制台自检 69 条断言（含新增的 4000 次写入压力检查）：0 失败。
- 定向压力：CLR 宿主里 1500 × 6 × 10 组「建包→加成员→finish→释放」，0 崩溃（修复前约每
  1500 次崩 1 次）。
- 交叉编译：Linux x86_64 / aarch64 动态库 + x86_64 静态库重新构建，导出符号核对 41 个一致。
- 打包：`build.ps1` 全流水线退出码 0，`dist/Andzip-v0.9.0-win-x64.exe` 19.66 MB，产物字节扫描
  确认内核已内嵌；绿色版启动后标题栏显示 `7-Zip 26.04 (x64) · Release · 60 formats / 7 writable`。
- azcore 命令行往返：中文文件名 + AES-256 口令的 ZIP，列出 5 条目、解压后 3 个文件 SHA-256
  逐一对齐。

## [0.9.0] - 2026-10-10

### 新增
- **压缩包视图（FR-4 首个切片 / M3）**：`Ctrl+O` 打开压缩包后主窗口由启动画面切换为压缩包视图——
  - **归档清单读取 `ArchiveReader`**（仅解析目录结构，不解压数据）：ZIP（含分卷按卷序拼接；**加密包无需密码即可读取条目元数据**）、TAR / TGZ / GZ；输出条目路径 / 名称 / 目录标记 / 原始大小 / 压缩后大小 / 是否加密 / 压缩方法 / 修改时间
  - **文件列表**：默认四列 `名称 / 压缩后大小 / 原始大小 / 类型`（对齐 FR-4.2 默认列）；目录行以 `\` 结尾，类型列显示「文件夹」，文件显示扩展名（无扩展名为「文件」）
  - **状态栏统计（FR-4.3）**：默认显示 `文件: n, 文件夹: n, 压缩文件大小: X`；选中文件时切换为 `已选择 n 个文件, X 字节 (压缩后大小: Y 字节, Z%)`
  - 窗口标题随打开的文件变化（`文件名 - Andzip (Professional)`）
- **视图 / 文件菜单接线**：
  - `F4` 关闭压缩文件（回到启动画面）
  - `F6` 启动画面 ↔ 压缩包视图切换（FR-1.5；未打开压缩包时保持启动画面）
  - `Ctrl+E` 解压文件：已打开压缩包时直接进入「选择解压路径」；未打开时先选择压缩包（原 `Ctrl+O` 的解压承载逻辑迁移至此）
- 启动画面「打开压缩文件」卡片接入打开流程（原先未接线）
- 语言包新增 11 键（中 / 英同步）：视图三列 / 文件夹 / 文件 / 压缩文件类型名 / 两条状态栏文案 / 打开失败与格式不支持提示

### 变更
- **`Ctrl+O`（打开压缩文件）语义回归参考行为**：由 v0.7.0 的「直接进入解压流程」改为「打开压缩包视图」（解压改由 `Ctrl+E` 承载）
- 版本号 0.8.0 → **0.9.0**（新增功能，SemVer 次版本 +1，§11.2）；`BuildNo` 8 → 9

### 修复
- **打包版打开压缩包即崩溃**（冒烟测试中发现，致命）：压缩包视图的表头与每个数据行**共享了同一个 `ColumnDefinitions` 实例**，Avalonia 的 `Grid` 不允许列定义跨 Grid 复用，布局阶段抛出未处理异常导致进程静默退出——改为每个 Grid 独立创建列定义
- 新增未处理异常日志（`%TEMP%\andzip-crash.log`）：单文件绿色版无控制台，崩溃信息落盘便于诊断（本次缺陷即依赖该日志定位）

### 测试
- 单元测试：**174 通过 / 0 失败 / 0 跳过**（较 v0.8.0 +9）
- 新增覆盖：ZIP 清单（条目 / 目录标记 / 计数与体积 / 压缩方法 / 加密标记无需密码）、分卷包清单拼接、TAR 与 TGZ 清单、GZ 清单（归档名去 `.gz` 与 ISIZE 原始大小）、不支持格式与归档缺失

### 产物
- `dist/Andzip-v0.9.0-win-x64.exe` — **18.92 MB**（较 v0.8.0 +0.01 MB，NFR-8 预算 ≤ 25 MB 达标）
- 端到端冒烟（UI Automation 实测，单文件裁剪产物）：
  - 打开分卷包 `split-out.zip`：标题 `split-out.zip - Andzip (Professional)`；表头 `名称 / 压缩后大小 / 原始大小 / 类型`；数据行 `large.bin / 8 MB / 8 MB / BIN`；状态栏 `文件: 1, 文件夹: 0, 压缩文件大小: 8 MB`
  - 选中数据行后状态栏 `已选择 1 个文件, 8388608 字节 (压缩后大小: 8391173 字节, 100%)`（对齐 FR-4.3 文案）
  - `F4` 关闭压缩文件 → 回到启动画面（标题恢复、两卡片）；打开 AES-256 加密包**不弹密码框**（仅读元数据）且列出 `hello.txt / 65 B / 35 B / TXT`；`F6` 两次在启动画面与压缩包视图间往返切换、标题同步正确
  - 首轮冒烟暴露的崩溃已修复后复测通过（含自建包 / 分卷包 / 加密包）

### 已知偏差（跟踪至后续里程碑）
- 压缩包视图首切片未含：树形目录（F7）、工具条、右键菜单、列设置与视图样式、状态栏右侧指示图标（FR-4.1 / 4.2 可选列 / 4.4 等）
- FR-4.5 压缩包内编辑操作（添加 / 删除 / 重命名 / 新建文件夹 / 复制粘贴 / 编辑 F3）待后续切片
- 其余沿用 v0.1.0 – v0.8.0 条目

## [0.8.0] - 2026-10-10

### 新增
- **解压密码提示（§3.2 对话框 #7）**：压缩包含加密条目时（ZIP，含分卷包）在解压前弹出「输入密码」对话框——复用密码对话框的**输入模式**（仅密码一项、留空提示必填、取消即中止解压）；密码探测失败（归档损坏等）不阻断流程，交由解压阶段报错
- **重名询问对话框「要解压的文件」（FR-3.7）**：解压过程中遇到同名文件时弹出（显示冲突文件名，按钮 `重命名 / 覆盖 / 跳过`），选择经 UI 线程回调（对话框以进度窗口为属主）交回解压引擎，解压线程同步等待
- 语言包新增 6 键（中 / 英同步）：询问框标题 / 同名提示语 / 重命名 / 覆盖 / 跳过 / 密码必填

### 变更
- 解压重名处理由 v0.7.0 的「安全默认（自动重命名）」改为「询问」（对齐参考行为）；Core 的询问回调机制在 v0.7.0 已就绪，本版接入 UI 对话框
- 版本号 0.7.0 → **0.8.0**（新增功能，SemVer 次版本 +1，§11.2）；`BuildNo` 7 → 8

### 测试
- 单元测试：**165 通过 / 0 失败 / 0 跳过**（较 v0.7.0 +5）
- 新增覆盖：密码探测（ZipCrypto / AES-256 加密包为真、加密分卷包为真、明文 ZIP 与 TAR 为假、缺失 / 损坏文件为假）

### 产物
- `dist/Andzip-v0.8.0-win-x64.exe` — **18.91 MB**（与 v0.7.0 持平，NFR-8 预算 ≤ 25 MB 达标）
- 端到端冒烟（UI Automation 实测，单文件裁剪产物）：
  - 解压加密包（输入密码）：`Ctrl+O` 选 `adv-aes.zip` → 「选择解压路径」→ 弹出「输入密码」实测（标题「输入密码」、**仅 1 个密码框（无确认密码）**、空密码点确定提示「请输入密码。」）→ 输入正确密码 → 解压完成、无错误
  - 产物：`extract-aes\adv-aes\hello.txt`（35 B）SHA-256 与原始文件一致（`A8FCBBD6…3BDC`）
  - 同名文件（询问对话框）：同一分卷包二次解压到同一目标 → 弹出「要解压的文件」实测（标题「要解压的文件」、提示「目标文件夹中已存在同名文件：」、文件名 `large.bin`、按钮 `重命名(&R) / 覆盖(&O) / 跳过(&S)`；询问框在进度窗口之上正常响应）→ 选「重命名」→ 解压完成、无错误
  - 产物：`extract-dup\split-out\` 下同时存在 `large.bin` 与 `large (2).bin`（各 8,388,608 B，SHA-256 均与原始文件一致）

### 已知偏差（跟踪至后续里程碑）
- 「要解压的文件」为逐文件询问（暂未提供「全部应用」类选项）
- 「解压后行为」（FR-3.6：解压后打开文件夹 / 不关闭进度窗口）依赖设置页（M4）接入；「选定的文件」依赖 M3 压缩包视图选中上下文
- 其余沿用 v0.1.0 – v0.7.0 条目

## [0.7.0] - 2026-10-10

### 新增
- **解压引擎（FR-3 / M2 首个切片）**：新增 `ArchiveExtractor` 门面，按扩展名分发——
  - **ZIP**（SharpZipLib 读取）：支持 ZipCrypto / AES-256 加密条目（需密码）、BZip2 / LZMA 等非 Deflate 条目读取、**分卷包自动按卷序拼接后解压**（复用 v0.5.0 分卷基础设施）
  - **TAR / TGZ / GZ**：`System.Formats.Tar` + gzip；GZ 单文件解压按 gzip 头中的原始文件名命名（无则取归档名去 `.gz`）
  - **智能解压（FR-3.3）**：`StripSingleRootFolder`——归档内所有文件都位于同一顶层目录时自动剥离该层（对齐设置页「如果压缩文件中只有一个根文件夹，则直接显示文件夹中的文件」默认开）
  - **重名策略（FR-3.7 基础）**：覆盖 / 跳过 / 自动重命名（`name (2).ext`）/ 询问（由 UI 回调决定；未提供回调时按安全默认自动重命名，不静默覆盖）
  - **安全与完整性**：路径逃逸（Zip Slip）拒绝、非法文件名字符清洗、条目修改时间写回；复选项「解压后删除压缩文件」生效；取消（CancellationToken）即中止
- **「选择解压路径」对话框（FR-3.1）**：目标路径（可编辑下拉）+ 文件夹选择按钮；左侧快捷位置（桌面 / 我的文档 / 我的图片 / 我的电脑）+ 右侧文件夹树（按需加载子目录）；「解压缩:」全部文件 / 选定的文件（无选中时禁用）+ 新建文件夹；复选项 解压到选定文件夹下与压缩包同名的文件夹（默认勾选）/ 解压后删除压缩文件 / 记住设置；确定 / 取消
- **解压入口（临时承载）**：文件→打开压缩文件（`Ctrl+O`）由禁用占位改为可用——选择压缩包后弹出「选择解压路径」并解压（进度窗口 + 可取消）；M3 压缩包视图接入后改为打开视图
- 通用消息框（错误提示）；进度窗口文案参数化（「正在解压...」）
- 语言包新增 18 键（中 / 英同步）：对话框标题 / 目标路径 / 快捷位置 / 解压缩区 / 新建文件夹 / 三个复选项 / 错误与状态文案

### 变更
- 版本号 0.6.0 → **0.7.0**（新增功能，SemVer 次版本 +1，§11.2）；`BuildNo` 6 → 7

### 测试
- 单元测试：**160 通过 / 0 失败 / 0 跳过**（较 v0.6.0 +24）
- 新增覆盖：ZIP 基本解压（文件 / 目录 / 计数 / 中文名）、加密解压（正确密码往返 / 无密码报错）、分卷解压按卷拼接、四种重名策略与无回调安全默认、智能解压剥离单根目录（ZIP / TAR）与混合结构不剥离、TAR / TGZ / GZ 往返、GZ 头原始文件名、Zip Slip 拒绝、非法字符清洗、不支持格式 / 归档缺失 / 取消 / 解压后删除源包

### 产物
- `dist/Andzip-v0.7.0-win-x64.exe` — **18.91 MB**（较 v0.6.0 +0.03 MB，NFR-8 预算 ≤ 25 MB 达标）
- 端到端冒烟（UI Automation 实测，单文件裁剪产物）：
  - 解压分卷包（正常路径）：`Ctrl+O` 选 `split-out.zip` → 「选择解压路径」对话框实测（标题「选择解压路径」、目标路径默认＝压缩包所在目录、左侧四个快捷位置「桌面 / 我的文档 / 我的图片 / 我的电脑」、右侧盘符树含 `C:\` `D:\`、「全部文件」选中且「选定的文件」禁用、三个复选项状态与默认值一致）→ 目标路径改为 `extract-out` → 确定 → 进度窗口正常关闭、无错误
  - 产物：`extract-out\split-out\large.bin`（8,388,608 B）——**分卷按卷序拼接解压正确**（SHA-256 与原始文件一致 `744B3ED2…0620`），「解压到选定文件夹下与压缩包同名的文件夹」语义生效
  - 加密包（错误路径）：选 `adv-zipcrypto.zip` 直接确定 → 弹出错误框（标题「错误」、正文「解压失败：No password available for encrypted stream」）；目标目录仅创建同名子目录、**无残留半成品文件**

### 已知偏差（跟踪至后续里程碑）
- 加密压缩包解压时的「输入密码」提示（§3.2 对话框 #7）尚未接入：当前无密码解压加密包会以错误消息提示失败，待下一版实现
- FR-3.7 的「要解压的文件」询问对话框尚未实现：重名默认自动重命名（不覆盖已有文件）
- 「选定的文件」（依赖 M3 压缩包视图选中上下文）、代码页处理（FR-3.4）、解压后行为（FR-3.6）、并行解压与内存上限（FR-3.5）待后续版本；TAR 符号链接条目暂不落盘
- 「记住设置」为会话内记忆（跨重启持久化随设置页 M4 实现）
- 其余沿用 v0.1.0 – v0.6.0 条目

## [0.6.0] - 2026-10-10

### 新增
- **高级设置面板（FR-2.5）**：「更多选项…」中的「高级设置 »」由禁用占位改为可用——点击在对话框右侧展开面板（标题「ZIP 压缩设置」，展开时窗口加宽、按钮文案变为「« 高级设置」可收起；切换到非 ZIP 格式时按钮自动禁用并收起面板）
  - **压缩方法**下拉：`仅存储 / Deflate（默认）`；选「仅存储」时忽略压缩级别（产物不压缩）
  - **加密算法**下拉：`ZipCrypto（默认，对齐参考软件实测值）/ AES-256`
  - **CPU 线程数**下拉：显示「自动」（禁用占位，待多线程压缩引擎 NFR-1）
  - **存储符号链接**复选框：禁用占位（符号链接入库需 Unix 模式位支持，列为后续事项）
  - **文件说明**复选框 + 注释输入框：勾选后注释框可用，写入内容保存为每个文件条目的 ZIP 注释
- **加密算法可选 ZipCrypto**：密码场景下默认 ZipCrypto（与参考软件默认一致）、可选 AES-256；ZipCrypto 产物同样通过「压缩后测试」的显式 CRC 校验（探测错误密码 / 数据损坏）
- 语言包新增 12 键（中 / 英同步）：面板标题、「« 高级设置」折叠按钮、压缩方法 / 加密算法 / CPU 线程数标签与选项、两个复选框

### 变更
- **ZIP 密码默认加密算法：AES-256 → ZipCrypto**（对齐 FR-2.5 参考实测默认值；需要更强加密时在高级设置中选择 AES-256）。v0.4.0 遗留偏差「密码仅 AES-256、不提供 ZipCrypto 兼容选项」就此消除
- 版本号 0.5.0 → **0.6.0**（新增功能，SemVer 次版本 +1，§11.2）；`BuildNo` 5 → 6

### 测试
- 单元测试：**136 通过 / 0 失败 / 0 跳过**（较 v0.5.0 +16）
- 新增覆盖：压缩方法与加密算法选项表（顺序 / 默认值）、默认方法 Deflate、仅存储方法产物不压缩且标准读取器可读、各方法往返与条目方法编号、密码默认 ZipCrypto（AESKeySize=0）、显式 AES-256、ZipCrypto 正确 / 错误密码、标准读取器无法还原 ZipCrypto 明文、条目说明写入与缺省为空、Store + AES + 说明组合、门面（扩展设置 + 压缩后测试）

### 产物
- `dist/Andzip-v0.6.0-win-x64.exe` — **18.88 MB**（与 v0.5.0 持平，NFR-8 预算 ≤ 25 MB 达标）
- 端到端冒烟（UI Automation 实测，单文件裁剪产物）：
  - 高级设置面板：点击「高级设置 »」右侧展开面板（标题「ZIP 压缩设置」、按钮文案变为「« 高级设置」）；面板默认值实测 压缩方法 `Deflate`、加密算法 `ZipCrypto`、CPU 线程数 `自动`（禁用）、存储符号链接（禁用）
  - 「文件说明」勾选后注释输入框由禁用变可用（输入 `E2E-adv-comment`）；AES-256 与 ZipCrypto 两轮带密码压缩均正常完成（含「测试压缩文件」），无错误提示
  - 产物体检（字节级解析）：AES 轮 `adv-aes.zip` 压缩方法 99 + 扩展字段 `0x9901`（AE-2 / vendor "AE" / strength 3 = AES-256）且中央目录含条目注释 `E2E-adv-comment`；ZipCrypto 轮 `adv-zipcrypto.zip` 压缩方法 8 + 加密位置位且无 AES 扩展字段（传统 PKWARE 加密）
  - 互操作（独立实现）：系统 `tar`（libarchive）以密码分别解密两轮产物，均还原 `hello.txt` 且 SHA-256 与原始文件一致（`A8FCBBD6…3BDC`）；ZipCrypto 错误密码报 `Incorrect passphrase` 且无明文泄漏

### 已知偏差（跟踪至后续里程碑）
- 压缩方法当前仅 `仅存储 / Deflate`：参考软件的 Deflate64 / BZip2 / LZMA / PPMd / Zstandard 需要额外压缩器（现用 SharpZipLib 1.4.2 仅支持**读取**这些方法，不支持写入），列为后续事项
- CPU 线程数 / 存储符号链接为禁用占位（多线程引擎 NFR-1、符号链接入库待实现）
- 其余沿用 v0.1.0 – v0.5.0 条目（TAR/TGZ/GZ 分卷、付费功能未实现、ZIPX/7Z/LZH/ISO/ZPAQ/EXE 未实现等）

## [0.5.0] - 2026-10-10

### 新增
- **分卷压缩（FR-2.3）**：压缩文件设置区「更多选项…」中的分卷下拉由禁用占位改为可用，档位与参考软件语言包实测一致（14 项）：`不分卷(默认) / 3 MB / 4 MB / 5 MB / 10 MB / 20 MB / 25 MB (Gmail) / 100 MB / 300 MB / 650 MB (CD) / 700 MB (CD) / 4 GB (FAT32) / 4.7 GB (DVD) / 自定义大小`
  - 「自定义大小」弹分卷大小对话框（输入 MB 整数，1 – 102400，越界给本地化提示；取消回退到上一个档位），确认后档位显示为「自定义大小 (N MB)」
  - ZIP 分卷命名采用 WinZip 风格：`name.z01, name.z02, …, name.zip`（`.zip` 为末卷；存在 `.z01` 即视为分卷包），分卷为完整 ZIP 字节流的顺序切分——按卷序拼接后与单文件 ZIP 等价
  - 加密（AES-256）可与分卷组合；压缩后测试 / 压缩后删除原始文件 / 独立压缩三个复选项与分卷组合可用
  - 覆盖写入前清理同前缀旧分卷（避免上次运行的 `z01…` 残留混入本次结果）；失败或取消自动清理已生成的卷与半成品
- **完整性测试增强**：ZIP 测试改用 SharpZipLib 逐条目读取并**显式校验 CRC**（.NET 内置读取器不校验 CRC，此前损坏的 stored 块无法检出）；分卷包自动识别并按卷序拼接后测试；加密条目校验密码 / AES 认证码
- 语言包新增 16 键（中 / 英同步）：13 个分卷档位 + 自定义大小 + 分卷大小对话框（标题 / 标签 / 校验提示）

### 变更
- 版本号 0.4.0 → **0.5.0**（新增功能，SemVer 次版本 +1，§11.2）；`BuildNo` 4 → 5
- 分卷档位字节数按 1024 进制换算（4.7 GB (DVD) 取 4.7 × 1024³），与参考软件的精确字节值待后续实测校准

### 测试
- 单元测试：**120 通过 / 0 失败 / 0 跳过**（较 v0.4.0 +14）
- 新增覆盖：分卷档位表（14 项 / 顺序 / 自定义）、多卷命名与卷大小（z01…zip，各卷等于分卷大小）、按卷序拼接内容往返、小于一个分卷时不产生分卷、加密 + 分卷往返、旧分卷清理、取消 / 失败清理、测试器对分卷的通过 / 损坏检出 / 缺首卷报错、门面组合（分卷 + 压缩后测试、分卷 + 删除源文件）

### 产物
- `dist/Andzip-v0.5.0-win-x64.exe` — **18.88 MB**（与 v0.4.0 持平，NFR-8 预算 ≤ 25 MB 达标）
- 端到端冒烟（UI Automation 实测，单文件裁剪产物）：
  - 格式联动：切到 TGZ 后「分卷」与「设置密码」均置灰禁用（分卷文本标注 disabled），切回 ZIP 恢复可用
  - 自定义大小：选「自定义大小」→ 输入 2 → 确定后档位显示「自定义大小 (2 MB)」
  - 分卷创建（8 MB 随机数据，勾选「测试压缩文件」）：产物 `split-out.z01…z04`（每卷 2,097,152 B）+ 末卷 `split-out.zip`（2,681 B），无错误提示（应用内测试器按卷序拼接 + CRC 校验通过）
  - 互操作（独立实现）：按卷序拼接后为合法 ZIP，`Expand-Archive` 解压出 `large.bin`，SHA-256 与原始文件逐字节一致（`744B3ED2…0620`）

### 已知偏差（跟踪至后续里程碑）
- 分卷当前仅 ZIP 生成（TAR / TGZ / GZ 分卷待与参考软件命名实测对齐后补充，UI 中分卷下拉在非 ZIP 格式下禁用）
- 高级设置面板（FR-2.5：压缩方法 / 加密算法 / CPU 线程数 / 符号链接 / 文件说明）仍为禁用占位，计划下一版实现（届时加密算法下拉将提供 ZipCrypto 兼容选项）
- 其余沿用 v0.1.0 – v0.4.0 条目（付费功能未实现、ZIPX/7Z/LZH/ISO/ZPAQ/EXE 未实现等）

## [0.4.0] - 2026-10-10

### 新增
- **ZIP 密码加密**（FR-2.6）：压缩文件设置区「设置密码(P)...」弹密码输入对话框（密码 + 确认密码；两次不一致显示本地化提示；留空确定 = 不加密），ZIP 格式下写入 WinZip **AES-256** 加密条目（对齐参考软件默认加密算法）
  - 引入 SharpZipLib 1.4.2（MIT 许可）；**无密码路径仍走 .NET 内置 System.IO.Compression，既有行为与产物不变**
  - 加密条目提前写入明文长度（WinZip AES 结构要求），目录条目不加密（与主流工具一致，便于直接查看结构）
- 密码按钮按格式可用性联动（FR-2.6）：仅 ZIP 可点（当前可创建格式中唯一支持加密的格式），切到 TAR / TGZ / GZ 自动禁用；切回 ZIP 保留已设密码
- 「压缩后测试压缩文件」支持加密 ZIP：测试器接受密码（AES 解密校验），密码错误 / 缺失均判失败
- 语言包新增 5 键（中 / 英同步）：密码对话框标题 / 密码 / 确认密码 / 不一致提示 / 确定

### 修复
- **切换压缩格式未自动同步输出文件名扩展名**（v0.3.0 已知偏差）：如 `新建压缩.zip` 切到 TGZ 自动变为 `新建压缩.tgz`；「浏览」对话框的默认扩展名与文件类型过滤同步跟随所选格式

### 变更
- 版本号 0.3.0 → **0.4.0**（新增功能，SemVer 次版本 +1，§11.2）；`BuildNo` 3 → 4
- 新增依赖：SharpZipLib 1.4.2（仅 ZIP 加密路径使用；单文件产物体积见下）

### 测试
- 单元测试：**106 通过 / 0 失败 / 0 跳过**（较 v0.3.0 +13）
- 新增覆盖：加密 ZIP 元数据（IsCrypted / AESKeySize=256）、正确密码往返（含中文内容与 Unicode 文件名、空目录条目）、无密码 / 错误密码不可读、标准读取器（System.IO.Compression）读不出明文、空密码 = 不加密、加密路径压缩级别体积差异、加密取消清理半成品、测试器带密码三态（正确 / 错误 / 缺失）、门面组合（压缩后测试 + 密码、独立压缩 + 密码逐包加密）

### 产物
- `dist/Andzip-v0.4.0-win-x64.exe` — **18.88 MB**（较 v0.3.0 +0.10 MB，NFR-8 预算 ≤ 25 MB 达标）
- 端到端冒烟（UI Automation 实测，单文件裁剪产物）：
  - 扩展名同步：`新建压缩.zip` →（切 TGZ）`新建压缩.tgz` →（切回 ZIP）`新建压缩.zip`；TGZ 下「设置密码」按钮自动禁用、切回 ZIP 恢复启用
  - 加密创建：添加 `hello.txt` → 设置密码（对话框确认 `E2E-Pass-123`，勾选「测试压缩文件」）→ 开始 → 产物 `enc-out.zip`（216 B）落盘且无错误提示（应用内测试器带密码校验通过）
  - 产物体检（字节级解析）：条目加密位已置位、压缩方法 99（WinZip AES）、扩展字段 `0x9901` = AE-2 / vendor "AE" / strength 3（AES-256）/ 真实方法 Deflate
  - 互操作（独立实现）：系统 `tar`（libarchive）正确密码解密还原 `hello.txt` 原文（32 B 一致）；错误密码报 `Incorrect passphrase` 且无法还原明文；`Expand-Archive` 与 System.IO.Compression 拒绝读取（不支持该方法，符合 AES ZIP 预期）

### 已知偏差（跟踪至后续里程碑）
- 分卷 / 高级设置仍为禁用占位；密码仅 AES-256，不提供 ZipCrypto 兼容选项（与参考软件默认一致，兼容模式待后续）
- ZIPX / 7Z / LZH / ISO / ZPAQ / EXE 尚未实现；其余沿用 v0.1.0 / v0.2.0 / v0.3.0 条目

## [0.3.0] - 2026-10-10

### 新增
- **多格式压缩引擎**：新增 TAR / TGZ / GZ（FR-2.2 / 附录 D.1）——TAR/TGZ 基于 `System.Formats.Tar`（Pax 格式，支持 Unicode 与长路径），GZ 为单文件 gzip；三种格式与 ZIP 共用目录展开 / 排除规则（ArchivePlanBuilder）
- **统一压缩门面 `ArchiveCreator`**：按格式分发，并实现「更多选项」三个复选项——
  - 压缩后测试（`ArchiveTester`：ZIP 逐条目读取含 CRC / 结构校验，TAR/TGZ/GZ 读取校验）
  - 压缩后删除原始文件（`SourceCleaner`：产物位于输入目录内时自动保护跳过）
  - 把每个文件 / 文件夹添加到单独的压缩文件（每输入一个包；文件取去扩展名主名 `one.txt → one.zip`，文件夹取目录名）
- 对话框「更多选项」三个复选项由禁用占位改为可用；格式选择接入（ZIP / TAR / TGZ / GZ 可创建，其余提示“暂未实现”）
- GZ 约束提示：多文件 / 文件夹选择 GZ 时显示本地化提示（`TEXT_ERR_GZ_SINGLE_FILE`）
- 语言包新增 1 键（中 / 英同步）

### 变更
- 版本号 0.2.0 → **0.3.0**（新增功能，SemVer 次版本 +1，§11.2）；`BuildNo` 2 → 3

### 测试
- 单元测试：**93 通过 / 0 失败 / 0 跳过**（较 v0.2.0 +18）
- 新增覆盖：TAR/TGZ/GZ 创建与内容往返、TGZ gzip 魔数与压缩级别差异、GZ 单文件约束（文件夹 / 多文件）、不支持格式约束、ArchiveTester（有效 zip/tar/tgz/gz；截断 zip 检出）、SourceCleaner（删除文件 / 目录、产物保护）、独立压缩（每输入一包 / 去扩展名命名 / 文件夹命名 / 与测试及删除源文件组合）

### 产物
- `dist/Andzip-v0.3.0-win-x64.exe` — **18.78 MB**（NFR-8 预算达标）
- 端到端冒烟（UI Automation 实测）：
  - TGZ：添加文件 → 格式切至 TGZ → 开始 → `out.tgz`（192 B，gzip 魔数 `0x1F8B`，Windows 内置 `tar -tzf` 可列出 `hello.txt`）
  - 「更多选项」面板：分卷（占位）/ 压缩级别 / 高级设置（占位）与三个复选项渲染、勾选正常
  - 独立压缩 + 压缩后测试：两个文件 → 生成 `hello.zip`、`second.zip`（各含对应条目），未生成合并产物，源文件保留

### 已知偏差（跟踪至后续里程碑）
- 切换压缩格式时未自动同步输出文件名的扩展名（参考软件会更新建议文件名）
- 设置密码 / 分卷 / 高级设置仍为禁用占位；ZIPX / 7Z / LZH / ISO / ZPAQ / EXE 尚未实现
- 其余沿用 v0.1.0 / v0.2.0 条目

## [0.2.0] - 2026-10-10

### 新增
- **ZIP 压缩引擎**（`Andzip.Core.Archive.ZipArchiveCreator`）：递归打包、单文件夹去根目录（对齐“只压缩单个文件夹时不要创建根文件夹”默认值）、默认排除 `Thumbs.db`、空目录入库、Unicode 文件名（UTF-8）、覆盖写入、取消即清理半成品（FR-2 / M1 首个切片）
- **「新建压缩文件」对话框**（`Ctrl+N` 与启动画面「新建压缩文件」卡片均可打开）：添加文件 / 文件夹（系统文件选择器）、列表（名称 / 大小 / 文件路径）、文件名 + 浏览、压缩格式下拉（11 项，仅 ZIP 可创建）、「更多选项」展开（压缩级别 4 档可选；分卷为占位）、开始 / 取消（FR-2.1~2.4）
- 压缩进度窗口（首版）：不确定进度条 + 取消（FR-2.7 种子）
- 压缩格式表与压缩级别表（Core，顺序与参考软件实测一致，附录 D.1 / FR-2.4）
- 语言包新增 33 个对话框键（中 / 英同步），并新增「语言键覆盖率」「菜单键覆盖率」测试

### 修复
- 对话框列头未跨列导致“名称 / 大小 / 文件路径”挤在一起（截图验证时发现，已修复）

### 变更
- 版本号 0.1.0 → **0.2.0**（新增功能，SemVer 次版本 +1，§11.2）；`BuildNo` 1 → 2

### 测试
- 单元测试：**75 通过 / 0 失败 / 0 跳过**（较 v0.1.0 +33）
- 新增覆盖：ZIP 引擎全场景（单文件 / 多层目录 / 去根目录 / 排除 Thumbs.db / 空目录 / 跳过输出自包含 / 4 档压缩级别 / 取消清理 / 覆盖写入 / Unicode / 计数与体积）、格式与级别表、输入项模型、语言键覆盖率

### 产物
- `dist/Andzip-v0.2.0-win-x64.exe` — **18.75 MB**（NFR-8 预算达标）
- 端到端冒烟（UI Automation 实测）：启动 → `Ctrl+N` 打开对话框 → 添加文件 `hello.txt` → 指定输出路径 → 开始 → 产物 `out.zip` 落盘（136 B，条目 `hello.txt` 18 B，可被标准工具读取）

### 已知偏差（跟踪至后续里程碑）
- 对话框部分控件为禁用占位（设置密码 / 分卷 / 高级设置 / 三个复选项），待对应功能接入
- 列表“大小”列暂用 B/KB/MB 英文单位（待本地化）；文件夹大小未递归统计
- 其余沿用 v0.1.0 条目（菜单快捷键显示、启动画面图标精修、macOS / Linux 产物随 CI 补出）

## [0.1.0] - 2026-10-10

### 新增
- 项目骨架与技术栈定选：.NET 10 + Avalonia 11.3（跨平台，见需求文档 §9.1 / v1.4）
- 启动画面：主题蓝背景、两个入口卡片（打开压缩文件 / 新建压缩文件）、展开箭头、底部「帮助页面」栏（FR-1.1，M0）
- 主菜单栏：7 个顶级菜单 + 全部菜单项 / 分隔线 / 快捷键展示，结构对齐需求文档附录 A（AC-2 骨架；未实现项按参考为禁用占位）
- 语言包机制：`.lang`（INI 风格）解析 + 多语言目录 + 缺省语言回退 + `${占位符}` 格式化 + `[br]` 展开；外部 `langs/` 目录优先、内嵌资源兜底（§7.3，FR-9 基础）
- 快捷键框架：参考文案（Ctrl+O / F4 / Ins / Shift+Ctrl+Del…）解析为跨平台手势；`Ctrl+W`（退出）已接入命令（附录 B 骨架）
- 研发流程基础设施：`VersionNo.ini`（Version / BuildNo）、`build.ps1`（测试门禁 → 单文件裁剪发布 → 体积报告）、`tools/make-icon.ps1`（自有图标生成）（§11.2 / §11.3）

### 修复
- 内嵌语言包失效：内嵌资源名带命名空间点号（`Andzip.App.langs.SimpChinese.lang`）时语言代码提取错误，界面回退显示键名；新增 `LangLoader.ExtractLanguageCode` 并补充回归测试
- 单文件产物启动崩溃（`DllNotFoundException: libSkiaSharp`）：启用 `IncludeNativeLibrariesForSelfExtract`，原生库随单文件内嵌、运行时自解压

### 变更
- 打包裁剪模式定为 `TrimMode=full`（与内嵌原生库组合已验证可独立运行；如需回退可用 `build.ps1 -TrimMode partial`）

### 测试
- 单元测试：**42 通过 / 0 失败 / 0 跳过**（`dotnet test Andzip.slnx -c Release`）
- 覆盖：语言文件解析、多语言目录与回退、占位符格式化、内嵌/外部语言包加载、快捷键解析、菜单结构完整性、版本文件解析

### 产物
- `dist/Andzip-v0.1.0-win-x64.exe` — **18.68 MB**（Windows 单文件绿色版，NFR-8 预算 ≤ 25 MB 达标）
- 冒烟验证：独立目录启动正常；窗口标题、7 个菜单、启动画面文案在「内嵌语言包」模式下显示正确

### 已知偏差（跟踪至 M6 打磨）
- 菜单快捷键由 UI 框架标准化显示（如 Ctrl+Delete），与参考文案（Ctrl+Del）存在显示差异，待自定义渲染修正
- 启动画面图标为简笔占位（`IconGeometry`），待按参考截图精修（§5）
- macOS / Linux 平台产物随 CI 补出（§9.2 跨平台说明）