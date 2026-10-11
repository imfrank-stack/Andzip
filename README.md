# Andzip

Bandizip 风格的 Windows 压缩工具。**界面是 C#（.NET 10 + Avalonia），压缩/解压内核是 C/C++**
（原样 vendor 的 7-Zip，外面包一层只有 41 个函数的扁平 C ABI）。

单个可执行文件、免安装、无广告、无捆绑：`dist/Andzip-v<版本>-win-x64.exe`，约 19.5 MB。

---

## 一、为什么做这个

想解决的是自己每天用的一个工具，顺便把一条工程路线走通：

- **用着顺手**。Bandizip 的交互（首页双栏、树 + 详情列表、右键直接解压、进度条跑完自己消失）
  是很多人已经习惯的形状，照着它做不需要用户重新学。
- **干净**。免安装单文件、只写 `HKCU`（不要管理员权限）；改默认程序只动你明确勾选过的格式，
  并且**先把原来的默认值记下来**，取消勾选时还回去；不碰别的软件的 ProgID，
  也不伪造 Windows 的默认应用保护哈希（那条路是绕过系统安全机制，走不得）。
- **能力来自真正的引擎，而不是自己造解码器**。7-Zip 的格式覆盖面是事实标准，直接复用它；
  本项目的工作在**接口层与产品层**：把 284 个编译单元的 C++ 收成一个稳定的 C ABI，
  再在上面长出界面、路径安全、重名策略、分卷、进度上报。
- **一条能被别人复用的工程记录**：7-Zip 怎么 vendor、扁平 C ABI 怎么设计、
  跨平台（Windows / Linux x86_64 / Linux aarch64）怎么在同一台 Windows 主机上构建、
  单文件裁剪打包哪些开关真的有效（以及哪些是白费功夫）。踩过的坑连症状带定位过程写在
  [`docs/开发记录.md`](docs/开发记录.md) 里。

**它不打算做的事**：成为 7-Zip 的替代品（功能还差得远）、做全平台原生 UI、
以及任何形式的「绕过系统安全机制来抢占默认程序」。

---

## 二、现状

| 项 | |
|---|---|
| 主要平台 | **Windows x64** —— 构建、测试、运行、打包都在本机验证过 |
| 格式能力 | 引擎报告 **60 种可读 / 7 种可写**（可写：ZIP、7Z、TAR、TGZ、GZ、BZ2、XZ） |
| 单文件绿色版 | 约 **19.5 MB**，已确认内核被真正内嵌 |
| 测试 | 899 项断言全绿（原生 532 + Core 298 + P/Invoke 外壳 69） |
| CI | GitHub Actions，Windows 跑与本地完全同一份门禁脚本 `ci.ps1` |
| Linux / macOS | 内核能交叉编译、CI 有真机作业；**界面从未在这些平台运行过** |

功能上它还是一个**前半段做完、后半段在补**的工具：打开、浏览、压缩、解压、文件关联、右键菜单可用；
包内编辑（增删成员）、预览、密码恢复一类还没开始。请看下面两张清单再决定要不要用。

### 已完成

- **浏览压缩包**：树形目录 + 11 列详情列表（列宽可拖拽、长文字不遮挡）、图标视图带缩略图、
  状态栏汇总；能作为单个归档打开 `archive.7z.001` 这类多卷包。
- **解压**：整包解压与逐条解压两条路径、百分比进度、可取消、重名逐个询问（覆盖/跳过/自动重命名）、
  zip-slip 路径防护、非法字符清洗、单顶层目录剥离、ZIP 的 `.z01` 风格分卷拼接、口令保护包先问再解。
- **压缩**：新建压缩文件对话框（格式、**5 档级别**、口令、分卷、排除 `Thumbs.db`、压缩后测试/删除源文件）、
  ZIP 分卷由 Core 切块实现。级别是 `仅存储 / 极速 / 快速 / 标准 / 极限`，对应引擎 `-mx=` 的
  0 / 1 / 3 / 5 / 9 —— 前三档拼速度，标准档看平衡，极限档只管压得小。
- **资源管理器集成**：文件关联（勾选即双击走我们，撤销时还原接管前的默认程序）；
  右键**智能解压** —— 包里只有一个文件就直接解出来，所有内容都在同一个顶层文件夹里就把那个文件夹
  整个放出来（不会再套一层同名文件夹），零散多个才新建一个以压缩包命名的文件夹收纳，重名依次用
  「名称 (2)」「名称 (3)」；右键**解压到当前文件夹**；右键**压缩为 ZIP / 7Z**（直接压）与
  **压缩到…**（打开「新建压缩文件」对话框，可设密码 / 分卷 / 级别，预填好输入与包名）。
- **设置窗口（F5）**：常规、文件关联、上下文菜单、压缩四页已实装。
- **不建主窗口的快路径**：双击直接解压、右键压缩 / 解压 —— 只有一根百分比进度条，跑完自动消失。
- **多语言**：中文 / 英文语言包，239 个键，键集与占位符逐一对齐。
- **工程化**：单文件裁剪打包、跨平台交叉编译、导出符号核对、CI 门禁、逐版本更新日志。

### 未完成 / 已知限制

- **不能往已有归档追加、删除成员或原地更新**；没有「添加到已有压缩包」「测试并修复」「生成 SFX」
  「密码恢复」。
- **压缩包视图只到「浏览」**：列表右键菜单、列显隐与列宽记忆、双击预览、拖拽解压、图像预览面板都没有。
- **条目「文件说明」（逐条目注释）不支持**，会回报警告而不是静默丢弃。
- **分卷只有 ZIP 真做**：7z / xz 的原生多卷输出未实现。
- **ZIP 的密码只支持 ASCII**（7-Zip 写入端自身的限制），用中文密码请改 7Z。
- **右键菜单是静态注册表动词**，不是 COM shell 扩展：Windows 11 上落在「显示更多选项」二级菜单里、
  一次只处理一个文件或一个文件夹、菜单标题渲染不出实际文件名。
- **默认程序可能被 Windows 挡住**：`FileExts\<ext>\UserChoice` 的优先级高于我们写的注册表值，
  而那个哈希伪造不了（这本来就是为了防第三方抢默认应用）。这类扩展名会在设置里明确列出来，
  并给一个跳到系统设置的按钮。
- **设置窗口还有 5 页是占位文案**（解压 / 查看 / 安全 / 高级 / 语言）；工具栏的「添加 / 删除 / 测试 / 扫描」仍是禁用占位。
- **符号链接与 POSIX 权限**没接到界面上。
- **错误框里的异常文本仍是英文**（内核回传的消息直接显示）。
- 完整的逐条清单见 [`docs/开发记录.md` 第六节](docs/开发记录.md)。

---

## 三、目录结构

```
Andzip/
├── README.md                   本文件（面向使用者与贡献者）
├── docs/开发记录.md             架构决策、踩坑定位过程、逐版本验证记录（维护者读这份）
├── CHANGELOG.md                逐版本变更记录
├── VersionNo.ini               Version + BuildNo，发布时注入程序集版本
├── Andzip.slnx                 5 个 C# 工程：App / Core / Native / 两套测试
├── build.ps1                   版本号 → 单元测试（硬门禁）→ 发布 → 体积核对
├── ci.ps1                      CI 门禁本体：本地和流水线跑同一份
├── run.ps1                     不开 IDE 也能构建并启动主程序
├── langs/*.lang                语言包（English / SimpChinese）
├── assets/andzip.ico           程序图标
├── src/
│   ├── Andzip.App/             Avalonia 界面（全部用 C# 代码构建控件，没有 XAML）
│   │   ├── MainWindow.cs       菜单 + 工具栏 + 首页/压缩包视图切换
│   │   ├── App.cs              启动分发：主窗口 / 后台任务（/task=）/ 注册表自愈
│   │   ├── ShellTaskRunner.cs  后台任务分发 → DirectExtract / DirectCompress
│   │   ├── Services/           Windows 平台集成：文件关联、右键动词（HKCU 注册表）
│   │   └── Views/              压缩包视图、设置窗口与各对话框
│   ├── Andzip.Core/            业务逻辑：压缩/解压门面、命名与重名策略、路径安全、分卷、
│   │                           语言包、菜单模型、设置存储
│   ├── Andzip.Native/          P/Invoke 外壳（41 个 az_* 函数的 C# 映射）
│   └── Andzip.Native.Tests/    外壳的托管断言（控制台自检程序）
├── tests/Andzip.Core.Tests/    xUnit
├── native/                     压缩内核（C/C++，细节见 native/README.md）
│   ├── include/andzip/andzip.h 唯一公开头文件：扁平 C ABI
│   ├── src/api/                C ABI 门面
│   ├── third_party/7zip/       原样 vendor 的 7-Zip，未修改一行
│   ├── cmake/ + CMakeLists.txt
│   ├── scripts/                vendor / 各平台构建 / 导出符号核对
│   ├── tools/azcore/           命令行工具（list / create / extract / test）
│   └── tests/native/           原生测试
├── build/                      CMake 输出目录（全部可再生，不入库）
└── dist/                       build.ps1 的产物目录
```

---

## 四、架构一眼版

分层是**单向**的，界面永远看不到 native 的类型：

```
  Andzip.App          Avalonia 界面
        │             只调用 Core 的门面与选项记录
  Andzip.Core         深模块：小的接口 + 大量实现
        │             决定「事情怎么做」：命名、重名、路径安全、分卷、进度
  Andzip.Native       P/Invoke 外壳
        │             41 个 az_* 函数，UTF-8，__cdecl，不透明句柄
  andzip_core.dll     7-Zip 引擎
                      决定「字节怎么变」：识别格式、编解码、CRC、固实块、多卷拼接
```

两个值得单独说的判断：

- **接缝放在 Core 的四个静态门面上**（`ArchiveCreator` / `ArchiveExtractor` / `ArchiveReader` /
  `ArchiveTester`）。换引擎时界面一行都不用改 —— 本项目实际上就这么做过一次。
- **错误模型是翻译而不是透传**。`NativeEngine.Translate` 把引擎错误码映射到 Core 原有的异常类型，
  P/Invoke 的错误码不会成为界面必须知道的契约。

完整推导（为什么接缝这么放、解压为什么有两条落地路径、`PROPVARIANT` 那个间歇性崩溃怎么定位的）
在 [`docs/开发记录.md`](docs/开发记录.md) 第三、七节。

---

## 五、怎么构建

### 前置

- **Visual Studio 2022/2026 的 C++ 工作负载**（引擎用）。CMake / Ninja 用 VS 自带的即可。
- **.NET 10 SDK**。
- 交叉编译 Linux 时需要 Zig（`native/scripts/provision-toolchain.ps1` 会下载便携版）。
- Windows 上建议用 PowerShell；下面命令在 Git Bash 里请写成 `powershell -ExecutionPolicy Bypass -File …`。

### 1. 先构建压缩内核

```powershell
pwsh native/scripts/build-windows.ps1 -RunTests
```

它会配置并编译 `andzip_core.dll`（约 284 个编译单元，需要几分钟）、跑 532 条原生断言、
核对导出符号恰好是头文件声明的 41 个 `az_*`，最后**把 DLL 暂存到
`src/Andzip.Native/runtimes/win-x64/native/`**。

最后一步是界面工程能找到引擎的前提。**跳过它的话 `dotnet build` 照样成功**，只是程序一起来
就显示「compression engine unavailable」。

### 2. 构建界面与业务层

```powershell
dotnet build Andzip.slnx -c Release
```

### 3. 跑测试

```powershell
dotnet test Andzip.slnx -c Release                      # xUnit
dotnet run --project src/Andzip.Native.Tests -c Release # 控制台自检，必须单独跑
```

第二条不能省：`Andzip.Native.Tests` 是不带 xUnit 适配器的控制台程序，`dotnet test` 对它
只会还原、不会运行。

### 4. 运行

```powershell
pwsh run.ps1          # 构建 + 启动主程序
pwsh run.ps1 -Check   # 只构建 + 跑两套测试，不开窗口
```

等价裸命令是 `dotnet run --project src/Andzip.App`。在 Visual Studio 里双击 `Andzip.slnx`
之后直接 F5 也能起主程序。

标题栏应当显示 `Andzip (Professional) · 7-Zip 26.04 (x64) · Release · 60 formats / 7 writable`；
如果显示 `compression engine unavailable`，就是第 1 步没做或没暂存成功。

> 仓库里有好几个 exe，别搞混：**主程序**是 `dist/Andzip-v<版本>-win-x64.exe`（发布版）或
> `src/Andzip.App/bin/Release/net10.0/Andzip.exe`（开发版）。
> `build/native/bin/azgui.exe` 是内核自带的最小验证界面，`azcore.exe` 是命令行工具，
> `Andzip.Native.Tests.exe` 是自检 —— 三个都不是主程序。

### 5. 打包单文件绿色版

```powershell
pwsh build.ps1                 # 版本 → 测试（不过就拒绝打包）→ 发布 → 体积核对
```

产物在 `dist/Andzip-v<版本>-win-x64.exe`。打包会扫描产物字节确认 `andzip_core.dll` 真的在里面 ——
这个失败原本只在运行时才暴露。

体积的实测结论（哪些开关有效、为什么不是「换掉链接库形式」能解决的）记在
[`docs/开发记录.md` 第四节第 5 条](docs/开发记录.md)。

### 6. 一次性跑完所有平台（可选）

```powershell
pwsh native/scripts/verify-all-targets.ps1   # Windows 动态 + Windows 静态 + Linux 三个目标
pwsh ci.ps1 -Static                          # 本地等价于 CI 的 Windows 作业
```

---

## 六、想贡献

- 改代码的**同一个提交**里请把 `docs/开发记录.md` 的对应描述、`CHANGELOG.md`、`VersionNo.ini`
  一起更新；过期的「未实现」「未验证」比没有文档更有害。
- 改完至少跑 `pwsh ci.ps1 -Static`。新增门禁步骤请走脚本里的 `Invoke-Gate` / `Invoke-NativeStep`：
  只查 `$LASTEXITCODE` 会假绿（可执行文件找不到时抛的是终止错误而退出码不变，这事真的发生过）。
- 动过 `native/` 的接口或构建，同时更新 `native/README.md`。
- GUI 部分目前的验证依赖人工点击，手动清单在
  [`docs/开发记录.md` 第九节](docs/开发记录.md)；欢迎把它自动化。

---

## 七、许可证与出处

`native/` 合并了多个上游，机器可读的来源记录在 `native/third_party/MANIFEST.json`。

| 组件 | 许可证 |
|---|---|
| 7-Zip 引擎（`C/`、`CPP/`，原样 vendor，未修改一行） | LGPL-2.1-or-later **附 unRAR 限制** |
| zstd / lz4 / brotli / lizard 等 | BSD-3-Clause / MIT / BSD-2-Clause 等 |
| 上述组件的 7-Zip 封装 | MIT |
| Zig（仅用于交叉编译，不进产物） | MIT |
| SharpZipLib（**只有测试工程引用**，生产代码不依赖） | MIT |

`src/`、`tests/` 以及 `native/src`、`native/include`、`native/tools`、`native/tests`
是本项目的原创工作，沿用 Andzip 自身的许可证。

发布前必须复核两件事：

1. **unRAR 限制**：可以解码 RAR，但不得据此支持 RAR 创建。要宣传 RAR 能力先对照条款。
2. **LGPL 路线**：`andzip_core` 以 DLL 形式动态链接。若改成静态链接发布，需要按 LGPL 提供相应便利。

> 界面与产品设计参考 Bandizip。上游原始压缩包（`cankao/`）与逆向参考语料（`Bandizip-RE/`）
> 都**不在本目录内**：日常构建不需要它们，只有重新 vendor 内核时才需要 `cankao/`。
