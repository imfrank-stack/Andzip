# Andzip —— 重构后的项目

Bandizip 风格的压缩工具：C#（Avalonia）界面 + C/C++ 压缩内核。本目录是**完整、自包含的
交付物**：把 `Andzip/` 整个目录拿走，其余内容都可以删除，项目仍然能构建、能测试、能打包。

压缩/解压的全部实现来自 `native/`（原样 vendor 的 7-Zip 引擎 + 一层扁平 C ABI）；
界面与业务逻辑来自 `src/`，但 `src/Andzip.Core` 的实现已经从
`System.IO.Compression` + SharpZipLib 换成 native 引擎。

---

## 一、当前状态一览

| 项 | 状态 |
|---|---|
| Windows x64 全量构建（引擎 + 界面 + 测试） | ✅ 已通过，0 警告（`PROPVARIANT` 修复后重建） |
| 单元测试 | ✅ **810 项断言全部通过**（原生 532 ×2 种链接方式 + Core 209 + 外壳 69），Core 连续 5 次运行稳定 |
| 间歇性 0xC0000005（测试主机崩溃） | ✅ **已定位并修复**，见第七节第 10 条；修复后 69,000 次写入往返 0 崩溃 |
| 单文件绿色版打包（trim + 压缩 + 内嵌 native） | ✅ **19.47 MB**，已验证运行时能加载引擎（标题栏报告内核版本与格式数） |
| 界面实际运行 | ✅ 绿色版直接启动并正常关闭；首页 / 打开压缩包 / 树导航 / 图标视图已逐屏截图核对；azcore 命令行往返 + SHA-256 逐文件复核 3/3 一致 |
| Linux x86_64 / aarch64 动态库、x86_64 静态库 | ✅ 修复后重新交叉编译，导出符号三侧核对一致（41 个）；❌ **未在这些平台运行过** |
| macOS | ❌ 本机无 Apple SDK，未构建 |
| 条目「文件说明」（逐条目注释） | ❌ 未支持（引擎侧限制），会回报警告而不是静默丢弃 |
| 7z / xz 等格式的分卷输出 | ❌ 未实现；只有 ZIP 由 Core 切块支持分卷 |

---

## 二、目录结构

```
Andzip/
├── README.md                   本文件
├── Andzip.slnx                 5 个工程：App / Core / Native / 两套测试
├── run.ps1                     不开 VS 也能跑：构建 + 启动主程序（-Check 只跑两套测试）
├── ci.ps1                      CI 门禁本体：引擎 → 导出核对 → 静态库 → 构建 → 测试 → 打包
├── build.ps1                   版本号 → 单元测试（硬门禁）→ 发布 → 体积核对
├── .gitignore                  只放行「已暂存的内核 DLL」，其余构建产物一律不入库
├── .github/workflows/ci.yml    Windows 跑 ci.ps1；Linux/macOS 真机执行内核
├── VersionNo.ini               Version + BuildNo，发布时注入程序集版本
├── CHANGELOG.md                逐版本变更记录
├── assets/andzip.ico           程序图标
├── langs/*.lang                语言包（English / SimpChinese，各 203 行、键集一致）
├── src/
│   ├── Andzip.App/             Avalonia 界面（全部用 C# 代码构建控件，无 XAML）
│   │   ├── MainWindow.cs       菜单 + 工具栏 + 首页/压缩包视图切换 + 打开/解压流程
│   │   ├── MnemonicText.cs     去掉 Win32 助记符标记（SimpleTheme 不解析 &）
│   │   ├── Services/           平台集成：Windows 文件关联（HKCU 注册表）
│   │   └── Views/              工具栏、首页、压缩包视图（树 + 列表 + 图标）、底部栏、设置窗口，
│   │                           以及新建压缩、解压路径、口令、进度、分卷、重名等对话框
│   ├── Andzip.Core/            业务逻辑（本次重构的主战场）
│   │   ├── Archive/            压缩/解压门面 + 压缩包目录树（ArchiveHierarchy）—— 见下一节
│   │   ├── Lang/               语言包解析与目录（INI 风格，带 [br] 换行）
│   │   ├── Menu/               主菜单模型与快捷键解析
│   │   ├── Settings/           应用设置存储（扁平 key=value，%APPDATA%\Andzip\settings.ini）
│   │   └── Versioning/         VersionNo.ini 的读取与校验
│   ├── Andzip.Native/          P/Invoke 外壳（41 个 az_* 函数的 C# 映射）
│   └── Andzip.Native.Tests/    外壳的 69 条托管断言（控制台自检程序）
├── native/                     压缩内核（C/C++，见 native/README.md）
│   ├── include/andzip/andzip.h 唯一公开头文件：41 个 az_* 函数的扁平 C ABI
│   ├── src/api/                C ABI 门面（约 3700 行）
│   ├── third_party/7zip/       原样 vendor 的 7-Zip，未修改一行
│   ├── cmake/ + CMakeLists.txt 构建配方（284 个引擎编译单元）
│   ├── scripts/                vendor / 生成源清单 / 各平台构建 / 导出符号核对
│   ├── tools/azcore/           命令行工具（冒烟测试 + 用法示例）
│   ├── tools/azgui/            最小 Win32 验证界面（手动核对压缩解压）
│   └── tests/native/           532 条断言的原生测试
├── tests/Andzip.Core.Tests/    xUnit，209 个用例
├── build/                      CMake 输出目录（native / native-static / linux-*，全部可再生）
└── dist/                       build.ps1 的产物目录
```

**为什么 C# 外壳在 `src/` 下而不是 `native/csharp/`**：Visual Studio 在解决方案没有 `.suo`
状态时，按**项目路径的字典序**挑默认启动项目。外壳工程原先在 `native/csharp/Andzip.Native`，
`n` 排在 `s` 前面，于是它抢到了启动项目 —— 而它是类库，双击 `Andzip.slnx` 后按 F5 只会得到
「无法直接启动带有『类库输出类型』的项目」。把 4 个 C# 工程都放进 `src/` 之后，
`src/Andzip.App/...` 成为字典序第一项，F5 直接起主程序。详见第七节第 12 条。

---

## 三、架构：接缝放在哪里

分层是**单向**的，界面永远看不到 native 的类型：

```
  Andzip.App（Avalonia 界面）
        │  只调用 Andzip.Core 的门面与选项记录
        ▼
  Andzip.Core  ◀── 深模块：小的接口 + 大量实现
        │  Archive/NativeEngine.cs 是唯一出口
        ▼
  Andzip.Native（P/Invoke 外壳，ArchiveReader / ArchiveWriter / Hasher）
        │  41 个 az_* 函数，UTF-8，__cdecl，不透明句柄
        ▼
  andzip_core.dll / .so / .lib（7-Zip 引擎）
```

### 为什么接缝放在 Core 的门面上

`ArchiveCreator` / `ArchiveExtractor` / `ArchiveReader` / `ArchiveTester` 四个静态门面
+ 一组选项/结果记录，就是界面需要的全部接口。换引擎时**界面一行都不用改**（本次只为了
进度条和格式表动了少量调用点）。这就是深模块的意义：接口一小段，实现里塞进 60 种格式、
7 种可写格式、25 个编解码器、加密、分卷、路径安全。

### 职责划分：引擎解码，Core 决定落盘

这是本次重构最关键的一条设计判断：

- **引擎负责**：识别格式、解码、编码、CRC 校验、固实块的一次性解码、7z 风格
  （`archive.7z.001`）多卷的自动拼接。
- **Core 负责**：条目命名、是否包含根目录、排除 `Thumbs.db`、不自我包含、重名策略
  （询问 / 覆盖 / 跳过 / `name (2).ext` 自动重命名）、非法文件名字符清洗、
  zip-slip 防护、单顶层目录剥离（智能解压）、WinZip 风格（`.z01`）分卷的切块与拼接。

理由：native ABI 的重名策略只有「覆盖 / 跳过 / 引擎自定的 `~1` 重命名」，表达不了参考
软件的「逐个询问」和 `name (2).ext`；而路径安全、命名规则本来就该由应用侧统一掌控。

### 解压的两条落地路径（有意为之，不是冗余）

- **整包原生解压**（默认）：一次调用，固实（solid）归档只解码一次。
- **逐条解压**：只有当本次确实需要改名、清洗非法字符或剥离顶层目录时才走 —— 因为那时
  目标文件名已经和归档里存的不一样，整包解压无法表达。

代价是固实归档逐条读会重复解码，所以判定条件收敛在 `ExtractionPlan.NeedsPerEntry`
一处，能走快路就走快路。

### 错误模型：翻译，而不是透传

`NativeEngine.Translate` 把引擎错误码映射到 Core **原有**的异常类型，界面既有的 catch
分支在换引擎后依然成立：

| 引擎结果 | Core 异常 | 界面处理 |
|---|---|---|
| `ErrorNotFound` | `FileNotFoundException` | 通用错误框 |
| 打开阶段的 `ErrorBadFormat` | `ArchiveConstraintException(FormatNotSupported)` | 「不支持的格式」（本地化） |
| 读取阶段的 `ErrorBadFormat` / `ErrorCrc` | `InvalidDataException` | 通用错误框 |
| `ErrorUnsupported` | `ArchiveConstraintException` | 「暂未实现」 |
| `ErrorPassword` | `ArchiveFailureException(Password)` | 通用错误框 |
| `ErrorCancelled` | `OperationCanceledException` | 静默返回 |

`Andzip.Native.AndzipException` 不会从 Core 漏出去 —— 否则 P/Invoke 的错误码就成了
界面必须知道的契约。

---

## 四、怎么构建

### 0. 前置

- Visual Studio 2022/2026 的 C++ 工作负载（引擎用）；CMake / Ninja 用 VS 自带的即可。
- .NET 10 SDK。
- 交叉编译 Linux 时需要 Zig（`native/scripts/provision-toolchain.ps1` 会下载便携版）。

### 1. 先构建压缩内核

```powershell
pwsh native/scripts/build-windows.ps1 -RunTests
# 本机只有 Windows PowerShell 时：
powershell -ExecutionPolicy Bypass -File native/scripts/build-windows.ps1 -RunTests
```

脚本会：导入 `vcvarsall` 环境 → 配置并编译 `andzip_core.dll`（约 284 个编译单元，几分钟）
→ 跑 532 条原生断言 → 用 `check-exports.ps1` 核对导出符号恰好是头文件里的 41 个 `az_*`
→ **把 DLL 暂存到 `src/Andzip.Native/runtimes/win-x64/native/`**。

最后一步是界面工程能找到引擎的前提；缺了它，`dotnet build` 依然成功，但运行时会提示
"compression engine unavailable"。

### 2. 构建界面与业务层

```powershell
dotnet build Andzip.slnx -c Release
```

`Andzip.Core` 引用 `src/Andzip.Native`，后者把 `runtimes/**` 下的原生库带到输出，
所以 `src/Andzip.App/bin/Release/net10.0/runtimes/win-x64/native/andzip_core.dll` 会自动出现。

### 3. 跑测试

```powershell
dotnet test Andzip.slnx -c Release                                    # 209 个用例（xUnit）
dotnet run --project src/Andzip.Native.Tests -c Release      # 69 条断言（控制台自检）
```

第二条**必须单独跑**：`Andzip.Native.Tests` 是不带 xUnit 适配器的控制台程序，
`dotnet test` 对它只会还原、不会运行。`build.ps1` 已经把两条都放进门禁里。

### 4. 运行

**命令行（推荐，可复现）**：

```powershell
powershell -ExecutionPolicy Bypass -File run.ps1        # 构建 + 启动主程序
powershell -ExecutionPolicy Bypass -File run.ps1 -Check # 只构建 + 跑两套测试，不开窗口
```

`run.ps1` 不依赖任何 IDE 状态，并且会先确认 `andzip_core.dll` 真的在
`src/Andzip.Native/runtimes/win-x64/native/` 里 —— 缺了就直接报错并给出重建命令，
而不是等程序起来后显示「压缩引擎不可用」。（等价的裸命令是
`dotnet run --project src/Andzip.App`。）

**在 Visual Studio 里按 F5**：双击 `Andzip.slnx` 打开后直接 F5 就能起主程序 —— 解决方案里字典序
第一项是 `src/Andzip.App/Andzip.App.csproj`，而 VS 在没有 `.suo` 状态时正是按**项目路径字典序**
选默认启动项目的（C# 外壳工程因此从 `native/csharp/` 移到了 `src/` 下，理由见第七节第 12 条）。

如果仍报「无法直接启动带有『类库输出类型』的项目」，看工具栏上启动项目下拉是不是 `Andzip.Native`：
那是**本地 `.vs\` 里残留的旧状态**（本次修复之前留下的），删掉 `Andzip\.vs\` 重新打开即可；
或者手动 `Andzip.App → 右键 → 设为启动项目`。交付目录里已经没有 `.vs\`。

**别搞混这几个 exe**（这是刚踩过的坑）：

| 路径 | 是什么 |
|---|---|
| `dist/Andzip-v<版本>-win-x64.exe` | **主程序**（单文件绿色版，交付物） |
| `src/Andzip.App/bin/<配置>/net10.0/Andzip.exe` | 主程序，未打包的开发版 |
| `build/native/bin/azgui.exe` | **不是主程序**：内核自带的最小验证界面，标题栏写「Andzip 压缩内核验证界面」 |
| `build/native/bin/azcore.exe` | 内核命令行工具（`list` / `create` / `extract` / `test`） |
| `src/Andzip.Native.Tests/bin/.../Andzip.Native.Tests.exe` | 69 条断言的控制台自检 |

标题栏应显示 `Andzip (Professional) — 7-Zip 26.04 (x64) · Release · 60 formats / 7 writable`。
**如果显示 `compression engine unavailable`，就是第 1 步没做或没暂存成功。**

### 5. 打包单文件绿色版

打包体积的实测结论（别再重复试错）：

| 开关 | 结果 |
|---|---|
| 自包含 + `PublishTrimmed` + `TrimMode=full` + `EnableCompressionInSingleFile` | 19.72 MB（基线） |
| 再加 `DebuggerSupport=false` + `EventSourceSupport=false` + `MetadataTrimming=complete` | **19.47 MB** ✅ 已进 `build.ps1` |
| 改框架依赖（`--self-contained false`） | ❌ **25.18 MB，反而更大**：不能裁剪，而 Skia / ANGLE / HarfBuzz / 内核这些原生库占了大头且单文件包里压不动 |
| `UseSystemResourceKeys=true` | ❌ 体积零收益，还会把框架异常文案换成资源**键**，而错误框是直接显示 `ex.Message` 的 |
| 手工摘掉 `mscordaccore` / `mscordbi` / `DiaSymReader` | ❌ 后两个本来就不进 bundle，实测差 30 字节，不值得为它加一个 MSBuild target |

包内体积构成（未打包前 44 MB）：`libSkiaSharp` 9.0 MB、`av_libglesv2` 5.2 MB、`coreclr` 4.4 MB、
`System.Private.CoreLib` 2.7 MB、`libHarfBuzzSharp` 1.7 MB、`andzip_core` 1.7 MB、
`Avalonia.Base` 1.5 MB —— 也就是说剩下的量基本都在渲染栈与内核上，再往下压要么换渲染后端，
要么放弃单文件。


```powershell
pwsh build.ps1                 # 版本号 → 单元测试（不过就拒绝打包）→ 发布 → 体积核对
pwsh build.ps1 -SkipTests      # 仅调试
```

产物 `dist/Andzip-v<版本>-win-x64.exe`，约 19.7 MB（体积预算 ≤ 25 MB）。

> 打包有一个必须记住的坑：`andzip_core.dll` 是以「内容项」的形式进入输出的，不是 RID
> 资产，所以 `IncludeNativeLibrariesForSelfExtract` **不会**把它打进单文件包，必须用
> `IncludeAllContentForSelfExtract=true`。`build.ps1` 里已加注释，并且发布后会扫描产物
> 字节确认 `andzip_core.dll` 真的在里面 —— 这个失败只在运行时才暴露，不能靠人记得。

### 6. 交叉编译 Linux（可选）

见 `native/README.md`：用 Zig 作交叉驱动，`x86_64-linux-gnu` / `aarch64-linux-gnu` 动态库
与 `x86_64-linux-gnu` 静态库都能从 Windows 主机编出来。想一次性把「Windows 动态 + Windows
静态 + Linux 三个目标」全过一遍并核对导出符号：

```powershell
powershell -ExecutionPolicy Bypass -File native/scripts/verify-all-targets.ps1
```

它按平台依次打印每一步的结果（编译、断言数、导出核对），任何一步失败都会抛错中止。
（这句「会抛错」是修出来的，不是天生的：早期版本只检查 `$LASTEXITCODE`，而可执行文件找不到时
`$LASTEXITCODE` 压根不更新，于是 Linux 三个目标整段没跑、脚本却报全绿 —— 见第七节第 14 条。）

### 7. 持续集成（CI）

门禁本体是 **`ci.ps1`**，本地和 CI 跑同一份 —— 这样「CI 绿」和「我机器上绿」是同一个命题，
不会出现只在流水线里才跑的隐藏步骤：

```powershell
powershell -ExecutionPolicy Bypass -File ci.ps1                          # 完整门禁
powershell -ExecutionPolicy Bypass -File ci.ps1 -Static                  # 再加静态库链接（CI 用这个）
powershell -ExecutionPolicy Bypass -File ci.ps1 -SkipEngine -NoPackage   # 只跑托管侧，最快
```

`.github/workflows/ci.yml` 三个作业：

| 作业 | 跑什么 | 是否卡门禁 |
|---|---|---|
| `windows` | `ci.ps1 -Static`：532 原生断言 ×2 种链接 + 41 导出核对 + 209 xUnit + 69 自检 + 打包，上传绿色版与内核二进制 | ✅ 失败即红 |
| `linux` | `build-unix.sh -T`：**在真 Linux 上执行** `andzip_core_tests`，再核对导出符号 | 原生层 ✅；托管层仅报告 |
| `macos` | `build-unix.sh -T`，内核从未在 macOS 上编译过 | 仅探测（`continue-on-error`） |

两个刻意的取舍，别顺手改掉：

- **托管测试在 Linux 上是 `continue-on-error`**。这 209 个用例从没在非 Windows 上跑过，里面
  有按 Windows 语义写的路径清洗断言（`<>:"|?*` 在 Linux 上是合法文件名字符）。先让它把真实
  结果报出来，读过之后再决定要不要升格成门禁 —— 直接开成硬门禁只会得到一片红，然后被人关掉。
- **Windows 作业用 `shell: powershell`（Windows PowerShell 5.1）而不是 `pwsh`**。仓库里的
  `.ps1` 都是按 5.1 写的并保持纯 ASCII，换 pwsh 等于换一个没验证过的执行环境。

要真正跑起来还差一步：**这个目录目前还不是 git 仓库**，所以 workflow 不会被触发。

```powershell
cd Andzip
git init -b main
git add .
git commit -m "Andzip: self-contained deliverable (native engine + Avalonia app)"
git remote add origin <你的仓库>
git push -u origin main
```

`.gitignore` 已备好：`build/`、`dist/`、`bin/`、`obj/`、`.vs/` 一律不入库（约 740 MB 可再生
内容）。**唯一被跟踪的原生二进制**是 `src/Andzip.Native/runtimes/win-x64/native/andzip_core.dll`
—— 少了它，托管侧作业没有引擎可加载，CI 的构建 / 测试 / 打包三步会全红。

---

## 五、已完成的功能

### 压缩（新建压缩文件对话框，FR-2）

- **可创建格式 7 种**：`7z`、`zip`、`tar`、`tgz`、`gz`、`bz2`、`xz`（`wim` 引擎支持但界面
  暂未列入）。旧实现只有 ZIP / TAR / TGZ / GZ。
- 压缩级别 4 档（仅存储 / 快速 / 正常 / 极限）映射到引擎的 0..9 级。
- 每格式独立的压缩方法表：ZIP 可选 Deflate / Copy / BZip2 / LZMA / LZMA2 / Deflate64 / PPMd，
  7Z 可选 LZMA2 / LZMA / PPMd / BZip2 / Deflate / Copy。方法表来自引擎实测，不是抄规范。
- 密码 + 加密算法：ZIP 支持 ZipCrypto 与 WinZip AES-256，7Z 支持 AES-256 与「加密文件名」。
- 分卷（ZIP）：WinZip 风格 `name.z01 … name.zip`，档位与参考软件一致，支持自定义 MB。
- 复选项：压缩后测试、压缩后删除源文件、每个输入单独成包、是否包含根目录、排除 Thumbs.db。
- 输出文件在输入目录内时不会自我包含。
- **进度上报**（新增）：字节数、总量、当前条目、百分比；界面从「不确定进度条」变成真实
  百分比条。
- 引擎忽略某项设置时通过 `ArchiveCreationResult.Warnings` 回传，界面显示而不是丢弃。

### 解压（FR-3）

- **可读格式 60 种**：zip / 7z / tar / gz / bz2 / xz / zst / rar / cab / iso / nsis / wim /
  chm / apfs / ext / fat / ntfs / udf / squashfs / dmg / vhd / vhdx / rpm / arj / lzh …
  格式由引擎**按内容嗅探**，不再靠扩展名 switch，所以没有扩展名或扩展名写错的包也能打开。
- 重名策略：询问（回调）/ 覆盖 / 跳过 / 自动重命名 `name (2).ext`。
- 非法文件名字符清洗（`<>:"|?*` 与控制字符 → `_`），结尾的点与空格也显式处理。
- zip-slip 防护：条目路径跳出目标目录时**抛异常**，不静默跳过。
- 智能解压：所有条目共用同一个顶层目录时才剥离那一层；顶层同时有文件与目录时不剥。
- 保留修改时间；空目录也会创建。
- 解压后删除压缩包（在归档句柄释放之后才删，避免删不掉）。
- 「只校验 CRC 不解压」（完整性测试）覆盖全部 60 种格式。
- 加密包识别更准：头加密的 7z 连打开都需要口令，这种「打开即失败」也被判定为需要口令。

### 首页与主工具栏（FR-1）

- 首页不再是「两张卡片」的欢迎页，而是与压缩包视图**同一套双栏骨架**的空状态：左树栏 +
  分隔条 + 右内容栏 + 底部栏。打开与关闭压缩包时窗口布局不跳。
- 蓝色主工具栏：打开（带下拉）/ 解压（带下拉）/ 新建 / 添加 / 删除 / 测试 / 扫描 / 查看 /
  当前语言。已接命令的三个真正可点，其余是禁用态而不是假装有响应。
- 菜单与工具栏共用同一批命令委托（`MainMenuModel` 的 CommandId），不重复实现第二套分发。
- 菜单文案过一遍 `MnemonicText.Strip`：语言包保留 Win32 的 `文件(&F)` 写法，而 Avalonia 的
  SimpleTheme 不解析 `&`，不过滤就会显示成「文件(&F)」。

### 压缩包视图（FR-4）

- **左侧目录树**：根节点是压缩包文件名，往下是包内目录。树由 `ArchiveHierarchy`（Core，
  带单元测试）从**扁平清单**重建 —— 很多 ZIP 只写文件路径、不写目录条目，照抄 `is_dir`
  为真的那些行会漏掉整层目录。
- **右侧 11 列详情**：名称 / 压缩后大小 / 原始大小 / 类型 / 修改日期 / 压缩方法 / 加密算法 /
  循环冗余检验 (CRC) / 属性 / 注释 / 附加信息。表头可点排序（目录在前、文件在后，各自按名称
  码位序），`..` 恒在首行。**列宽可拖拽**（表头每列右边一条 5px 分隔列，松手后落到实例字段上
  再重画列表），窗口窄到放不下全部列时列表出自己的横向滚动条，表头按同一位移量反向平移对齐。
- **图标视图**：图像条目回读原始字节生成缩略图。`ArchiveContentSession` 只开一个归档句柄、
  串行读（引擎句柄不是线程安全的）、`Bitmap.DecodeToWidth` 直接缩到 128px；只在切到图标视图
  时才建立，切目录取消上一批请求，超过 400 条或单条超过 32 MB 就退回通用图标。
- **底部栏三个按钮**：详情列表 / 图标视图 / 在系统文件管理器里定位压缩包。状态文字是
  「文件: N, 文件夹: M, 压缩文件大小: X」，X 取引擎报告的包体物理大小。
- 点树节点与双击文件夹行 / `..` 行都汇到同一个导航入口。
- 名称列是「图标 + 文字」的两列 Grid，**不是** StackPanel：StackPanel 测量子项时给无限宽度，
  `TextTrimming` 永远不会触发，长文件名会直接压到隔壁列上。
- **打开流程**：系统文件框只列引擎报告的受支持扩展名（115 个，含 `.tgz` / `.txz` 这类副
  扩展名）；头加密的包在打开时就问口令（先试一次，引擎要口令才弹框，取消即放弃）。
- 命令行传入压缩包路径可直接打开 —— 这是文件关联的入口，也是没有扩展名的包的兜底路径。
- 条目方法从 ZIP 规范编号改成引擎方法名（`LZMA2:24`、`Deflate`、`Store`），
  不再有 `NoZipMethod = -1` 这种为 tar/gz 硬凑的占位值。
- 嵌套归档透明化：`.tgz` / `.tar.gz` / `.tar.xz` 自动展开内层清单，解压与测试同样按内层处理。
- 分卷读取：`archive.7z.001` 由引擎自动取回兄弟卷；WinZip 的 `.z01` 由 Core 拼接。

### 设置与文件关联（FR-5，第一片）

- **选项 → 设置（F5）**：`Views/SettingsWindow.cs`，左侧蓝色导航 + 右侧页面 + 底部「重置 / 确定」，
  导航顺序与参考软件一致（常规 / 文件关联 / 上下文菜单 / 解压 / 压缩 / 查看 / 安全 / 高级 / 语言）。
- **文件关联页已实装**：35 项候选分成 压缩包文件 / UNIX / 其它 / CD/DVD 四组，候选集由
  `FileAssociationCatalog.Supported(引擎可读扩展名)` 过滤得出 —— 引擎读不了的格式不会出现在列表里，
  也就勾不出一个「关联了却打不开」的项。
- **只登记到「打开方式」，不抢默认程序**：写 `HKCU\Software\Classes` 下每格式一棵
  `Andzip.<id>` ProgID 子树，并把 ProgID 挂进该扩展名的 `OpenWithProgids`；**不改**扩展名键的默认值。
  取消勾选时整棵删掉并移除挂的值，不碰别人登记的 ProgID。改完调用 `SHChangeNotify` 让资源管理器立刻刷新。
- 「立即应用」只在勾选与已保存状态不一致时可用；「确定」会顺手应用未保存的改动再关窗，
  右上角 X 才是放弃改动；「重置」恢复**出厂默认勾选**（不是放弃改动）。「Windows - 设置默认程序」
  打开 `ms-settings:defaultapps`。
- 目录刻意不含 APFS / NTFS / ELF / PE(dll,sys) / VHD 这类「能嗅探出来但不是压缩包」的格式，
  也不含 Compound（它的扩展名是 doc / xls / ppt）。**它们仍然能打开** —— 打开对话框与命令行走的是
  内容嗅探，与关联表无关。
- 设置存储是扁平 `key = value` 文本（`Andzip.Core.Settings.AppSettings`），落在
  `%APPDATA%\Andzip\settings.ini`。没用 JSON：反射式序列化在 `PublishTrimmed` 下要额外保留配置，
  为一堆标量不值当；而且语言包与 VersionNo.ini 本来就是这套语法。

### 其它

- 语言包、主菜单模型、快捷键、版本号读取：原样保留，测试全绿。
- `ArchiveEngine`（新增）：界面可查询引擎是否可用、版本、格式/编解码器数量，并设置线程数
  与内存上限。「线程数」下拉框从此真正生效（原先是禁用占位）。

---

## 六、未完成 / 已知限制

**读之前请先确认你要做的功能不在这里。**

### 压缩内核侧

1. **ZIP 的密码只支持 ASCII**。7-Zip 的 zip 写入端显式检查
   （`ZipHandlerOut.cpp` 的 `IsSimpleAsciiString`），非 ASCII 直接 `E_INVALIDARG`，
   ZipCrypto 与 AES 都拦。Core 已提前翻译成可本地化的
   `ArchiveConstraintException(NonAsciiPasswordNotSupported)` 并提示改用 7Z。
   **7z 的中文密码完全正常**（已实测往返）。
   → 这是相对旧 SharpZipLib 实现的**功能回退**，旧实现可以写中文密码的 zip。
2. **条目「文件说明」不支持**。7-Zip 的 out-handler 属性表里没有归档注释项，逐条目注释
   只在 ZIP 上有一条按字节截断的窄路径。现在写这类包会成功，但 `Warnings` 里会明确说
   「entry comments are not supported」，不会静默吞掉用户输入。
3. **分卷输出只有 ZIP**，且是 Core 把完整归档切成块 —— 不是引擎的卷机制。7z/xz 的原生
   分卷（`archive.7z.001`）写入未实现：直接驱动 `CMultiOutStream` 曾出现「报告成功但
   一个文件都不生成」的丢数据故障，因此内核层现在对 `volume_size` 一律返回
   `AZ_ERROR_UNSUPPORTED`。**读取**分卷是好的。
4. **不能往已有归档追加、删除成员或原地更新**。引擎的 `UpdateItems` 能做，但那要先读出
   旧归档再重放成员，尚未实现。
5. **`-mx=0`（仅存储）对 gzip/bzip2/xz 不改变体积**：这些单流格式的编码器各级别都走
   同一套压缩，tar 层本来就不压缩。所以 TGZ 的「仅存储」只是被接受，不会让包变大。
6. **7-Zip 能容忍被截断的中央目录**：截掉尾部（含 EOCD）的 zip 可能仍然打开成功。数据
   本身被破坏时 CRC 会发现。测试里已把这个行为钉住，避免将来被误判为回归。
7. **额外编码器（zstd / lz4 / brotli / lizard 的编码）需 `-ExtraCodecs`**，本次未开启验证。
   Zstandard 的**解码**可用。

### 界面与业务侧

8. **压缩包视图只到「浏览」**：右键菜单、列设置（显隐与列宽记忆）、双击文件打开
   （解压到临时目录再交给系统关联程序）、拖拽解压、图像预览面板都没有；工具栏的
   「添加 / 删除 / 测试 / 扫描」与语言按钮仍是禁用占位。
9. **没有「添加到已有压缩包」「测试并修复」「生成 SFX」**。
10. **符号链接与 POSIX 权限**：界面里的「存储符号链接」复选框仍是禁用占位；引擎侧
    `posix_mode` 有通道但界面没接。
11. **进度按条目/字节上报，但逐条解压路径下没有单条目内部的字节进度**（外壳的
    `ReadEntry` 不接回调）。
12. **解压口令仍是「先探测再一次性输入」**，没有用引擎的口令回调做「解压中途要口令」。
13. **`VersionInfo` 与 `ArchiveTester` 门面尚未被界面调用**（`VersionInfo` 只有测试在用）。
14. **语言包本轮新增 21 个键**（工具栏文案、压缩包视图列名与附加信息），同时删掉不再使用的
    `TEXT_HELP_PAGE` / `TEXT_STARTUP_OPEN` / `TEXT_STARTUP_NEW` / `TEXT_ALL_FILES`；
    其它语言的包（如 Bandizip 自带的 Russian）未同步。
15. **异常文本仍是英文**：内核回传的消息直接进错误框，只有少数约束类错误走了本地化文案。
19. **设置窗口只有「文件关联」是实装的**：其余 8 个导航页显示占位文案，按参考软件的页面顺序逐个补。
    参考软件关联页上的「基本选项」按钮没有跟着做 —— 它的行为无法从截图确定，放一个不知道会不会有用
    的按钮不如先不放。
20. **不做「设为默认程序」**：只登记到右键「打开方式」。Windows 10/11 对默认应用有哈希保护，
    静默改写既不礼貌也容易被系统回滚。删除关联项也不会清理用户手动改过的默认值。

### 平台

16. **Linux / macOS 的运行时在本机验证不了**。这台机器没有 WSL 也没有 Docker，跑不了 Linux
    二进制；macOS 连编译都没做过。CI 里已经挂了 `linux` / `macos` 两个作业去真机执行
    `andzip_core_tests`，但**在仓库推上远端之前它们一次都没跑过**，所以这条只能算「有手段了」
    而不是「验证过了」。顺带说清一个已经修掉的事实：在改名之前，即使跑起来，.NET 侧也永远
    加载不到内核（产物叫 `andzip_core.so`，外壳探测的是 `libandzip_core.so`），
    见第七节第 13 条。
17. **Windows arm64 只配置未构建**。
18. **静态库消费方必须整体链入**：注册 handler 的目标文件不被任何符号引用，普通静态链接
    会丢掉它们，结果是「库能加载、格式表是空的」。CMake 目标已用 `/WHOLEARCHIVE` 处理；
    手工集成要加 `/WHOLEARCHIVE:andzip_core.lib` 或
    `-Wl,--whole-archive libandzip_core.a -Wl,--no-whole-archive`。

---

## 七、重构过程中遇到的问题（以及怎么定位的）

记录过程而不只是结论，因为多数坑的「症状」和「原因」隔了好几层。

### 1. ZIP 里的中文文件名被其它工具读成乱码

**症状**：我们自己的界面、命令行工具、甚至 GNU 工具链都正常，但用 BCL
`System.IO.Compression` 和 SharpZipLib 打开我们写的 zip，`中文名字.txt` 变成乱码。

**定位**：写了一个诊断测试，把同一个包分别用三种实现读一遍，发现文件里存的**字节其实是
正确的 UTF-8**，但 zip 的「语言编码标志位」（通用标记位 bit 11）没有置上，于是别的实现按
OEM 代码页（这里是 GBK）解 UTF-8 字节。

**原因**：7-Zip 的 zip 写入端默认用 `CP_OEMCP` 转名字，且只有显式要求时才走 UTF-8 并置标志位。

**修复**：内核的 `BuildProperties` 对 zip 下发 `cp=65001`（并附 `cu=on` 表达意图）。
注意 `cu=on` 单独用在这个版本里**不生效**，`cp` 才是决定性的；而 `cp` 必须传数值，
传字符串会被判 `E_INVALIDARG`。

### 2. 往 tar 里加一个目录，整个写入失败

**症状**：`az_writer_finish` 返回 `ErrorInvalidArg (Win32 87)`，但同样的目录压成 zip/7z 正常。

**定位**：用命令行工具单独压 tar 复现，再读 7-Zip 的 `TarHandlerOut.cpp`：它在
`newData=1` 时要求 `kpidSize` 必须是 `VT_UI8`，而我们的回调对目录返回 `VT_EMPTY`。
zip / 7z 不较真，tar 较真。

**踩过的弯路**：先改成「目录报 `newData=0`」，结果原生测试**直接进程崩溃** —— 因为
`newData=0` 会让 handler 去 `indexInArchive` 里取数据，而我们所有条目的该值都是
「不在归档中」。

**正确修复**：`kpidSize` 一律返回 `VT_UI8`，目录返回 0。

### 3. 归档注释把整次写入带崩

`az_create_options.comment` 原先被映射成属性 `"c"`，但 7-Zip 的 zip 与 7z out-handler
属性表里都没有 `"c"`，未知属性直接 `E_INVALIDARG`。改成不下发、只回一条「被忽略」的警告。

### 4. `-mx=0` 与 `m=Deflate` 互相覆盖

**症状**：选「仅存储」压 200 KB 文本，产物 397 字节 —— 和「正常压缩」一模一样。

**定位**：分别用外壳（只给 Level=0）和 Core（Level=0 + 默认方法 Deflate）压同一个文件，
外壳得到 200,148 字节、Core 得到 397 字节，说明是**同时下发方法名和级别 0** 时，
7-Zip 按 Deflate 的默认级别压了，把「不压缩」吃掉了。

**修复**：级别为 0 且用户没显式选方法时，推断成 `Copy` —— 但**只有该方法在该格式的方法表里
存在时才推断**：tar 没有压缩方法属性，给它下发 `m=Copy` 会重新触发问题 2 那类
`E_INVALIDARG`。

### 5. 分卷：切块函数把刚写好的归档删了

`SplitVolumeSupport.Split(source, archive, …)` 会先删 `archive` 再切。我最初把同一个路径
同时当 source 和 archive 传入，于是 `FileNotFoundException`。修复是复合格式与分卷都先写到
中间文件，切完/压完再删中间文件；中间文件用 `<stem>.tar` 这类有意义的名字，因为它会成为
外层归档的成员名。

### 6. 单文件绿色版找不到压缩内核

`dotnet build` 和**非**单文件的 `dotnet publish` 都正常，只有单文件包运行时报
"compression engine unavailable"。原因：`andzip_core.dll` 是以 `None + CopyToOutputDirectory`
的形式进入输出的，属于「内容」而不是 RID 资产，`IncludeNativeLibrariesForSelfExtract`
只管后者。改用 `IncludeAllContentForSelfExtract=true`，并在 `build.ps1` 里加了发布后扫描
产物字节的断言。

### 7. 测试主机中途崩溃，用例数从 178 掉到 97

一次全量运行里报告数骤降。逐个筛除后发现是**问题 2 的中间修复**（`newData=0`）导致的原生
崩溃，不是测试框架问题。教训：用例总数突然变化本身就是信号，不能只看「失败 1」。

### 8. 若干「测试断言的是旧实现的细节」

这类不是 bug，但必须逐条判断「行为变了」还是「实现变了」：

- 压缩级别记录里带着 `System.IO.Compression.CompressionLevel` —— 是泄漏到接口上的类型，删掉。
- 条目方法用 ZIP 规范编号 + `-1` 占位 —— 换成引擎方法名。
- 「可创建格式只有 ZIP/TAR/TGZ/GZ」—— 这正是本次要改的东西，改成钉住「7Z/BZ2/XZ 现在可写」。
- 「zip 默认方法必须是 Deflate」—— 7-Zip 对压不动的数据会自动退回 Store，这是正确行为，
  于是把测试数据换成可压缩内容，并补一个测试专门钉住这个回退。

### 9. 外壳的选项块被提前释放（use-after-free）

`ArchiveWriter.Create` / `ArchiveReader.Open` 原本写成 `using var args = NativeArgs.Create(options)`，
方法一返回就把 `az_create_options` 连同里面的 `az_callbacks`（进度回调函数指针 + ctx）释放了，
而内核是在之后的 `Finish()` / `ExtractTo()` / `Test()` 里才回调它。改成把 args 的**所有权交给返回的
句柄对象**：`_ownedArgs` 字段，`Dispose()` 里先 `az_writer_free` / `az_archive_close`，再释放 args；
构造失败的路径用 `catch { args?.Dispose(); throw; }` 兜住。

这个改动是对的，但它**没有**让崩溃消失 —— 说明还有第二个原因。

### 10. 间歇性 0xC0000005：内核里未初始化的 `PROPVARIANT`

现象：`dotnet test` 每隔几次就报「测试主机进程崩溃」，用例数从 178 掉到 169/172/164……
栈顶固定在 `az_writer_finish`。定位过程（值得照着复用）：

1. **缩小到单个测试类**：只跑 `ZipEncryptionTests`（同一个集合，串行）仍然崩 —— 排除并行竞争。
2. **写不依赖 C# 的最小复现**：C 语言直调 ABI，把「建包→加成员→finish→释放」循环 20,000 次，
   动态 `LoadLibrary` 加载、每次成功后调 `az_last_error_message()`，仍然一次都不崩。
   结论：需要 CLR 宿主才会崩，问题在“两边都会走、但内存状态不同”的地方。
3. **按维度二分**：`mt` / `solid` / 有没有进度回调 / 有没有密码 全部无关；
   **格式是 zip 就崩，7z 基本不崩**。
4. **看崩溃地址**：用 `DOTNET_DbgEnableMiniDump` 收全量转储，`dotnet-dump` 取寄存器与模块表。
   转储里的 RIP 已经是 createdump 自己的等待调用（拿不到出错现场），本机又不是管理员，
   WER LocalDumps 与 PageHeap 都用不了 —— 于是改用**进程内自检**：
5. **在宿主里校验堆**：P/Invoke `ucrtbase!_get_heap_handle` + `kernel32!HeapValidate`，
   每次操作后校验 CRT 堆与进程默认堆。堆看起来是好的，说明破坏发生在“还没写坏链表的
   那一步”—— 也就是野指针释放，而不是越界写。
6. **回到代码里找「对未初始化内存做释放」的形状**：`native/src/api` 里有一处惯用写法
   （8 个局部变量 + 3 个属性辅助函数 + `PropEntry::Value`）：

   ```cpp
   PROPVARIANT prop;
   ::PropVariantClear(&prop);   // ← 读的是没初始化的 prop.vt
   ```

   `PropVariantClear` 会**按 `vt` 分派释放**：`vt` 恰好落在 `VT_BSTR`/`VT_LPWSTR`/`VT_UNKNOWN`
   这些分支时，它会对一个垃圾指针执行 `SysFreeString`/`CoTaskMemFree`/`Release`。
   这就是全部现象的解释：`CObjectVector::AddNew()` 用的是 `new T`（默认初始化，POD 成员
   **不清零**），而栈上的局部变量同理。纯 C 程序里这些内存大多是新页的 0（`vt==VT_EMPTY`
   → 无害），CLR 进程里同一个堆/同一批栈页塞满了托管帧与 LocalAlloc 残留，`vt` 经常是垃圾值
   → 千次左右踩中一次。ZIP 分支每次写入要设 6～10 个属性，所以明显比 7z 容易崩。

   修法：把“先 Clear 再用”换成“**先 Init 再用**”。局部变量改成
   `::PropVariantInit(&prop)`（只写 `vt = VT_EMPTY`，不读任何字段），`PropEntry` 补默认构造函数
   `PropEntry() { ::PropVariantInit(&Value); }`，三个 `Add*Prop` 辅助函数里的 Clear 全部换成 Init；
   后续复用变量时的 Clear 保留（此时 `vt` 已经是合法的）。POSIX 侧在 `az_pch.h` 补了
   `PropVariantInit` 的内联垫片，Linux x86_64 / aarch64 交叉编译已验证通过。

   验证：改前 1500 次的组合 5 组里崩 3～4 组；改后同一批配置 5 × 6 × 1500 = 45,000 次，加上
   常驻检查的 6 × 4000 = 24,000 次，合计 **69,000 次往返 0 崩溃**，
   `dotnet test Andzip.slnx` 连续 5 次稳定 178/178。这一类缺陷已经固化成一条常驻检查
   （`Andzip.Native.Tests` 的 `writer stress`，4000 次写入），做法与反向验证见第九节。

   两条可沉淀的经验：①「纯 C 复现不了」不等于「不是内核的 bug」，堆的**内容**才是变量；
   ② 没有调试器权限时，`_get_heap_handle`/`HeapValidate` + 逐调用自检 + 代码里搜
   “对未初始化结构调释放函数”这个模式，比死磕栈回溯更快。

   附带修掉一个长期存在的 `C4819`：`az_writer.cpp` 里有两段中文注释，而内核工程没有加
   `/utf-8`，编译器按本地代码页（936）读源码会报警。既然 `native/` 的注释约定是英文，
   就把那两段翻译成英文（内容一字不差地保留），整个 `native/src`、`native/include` 现在
   是纯 ASCII，构建 0 警告。

### 11. `Andzip.Native.Tests` 不在 `dotnet test` 的射程里

它是**不带 xUnit 的控制台自检程序**（`OutputType=Exe`，69 条断言）。`dotnet test` 对它只能
“还原，然后什么都不做”，所以「测试全绿」曾经只覆盖了 Core 的 178 个用例。现在
`build.ps1` 会显式把它跑一遍并把非零退出当失败。

### 12. 双击 `Andzip.slnx` 后 F5 报「无法直接启动带有『类库输出类型』的项目」

第一反应是「`.slnx` 记录不了启动项目，所以只能让用户自己右键设为启动项目」—— **这个结论是错的**，
它把可以修的东西推给了用户。真正的机制看 VS 工具栏就知道：启动项目下拉里是 **`Andzip.Native`**，
一个类库。为什么会是它？

对比两个解决方案就清楚了：

| 解决方案 | 工程路径排序后的第一项 | VS 默认启动项目 | F5 |
|---|---|---|---|
| 根目录旧 `Andzip.slnx`（只有 `src/...`、`tests/...`） | `src/Andzip.App/Andzip.App.csproj` | Andzip.App | ✅ |
| 重构后的 `Andzip.slnx`（外壳在 `native/csharp/...`） | `native/csharp/Andzip.Native/...` | **Andzip.Native（类库）** | ❌ |

**VS 在没有 `.suo` 状态时按项目路径的字典序选默认启动项目**，而不是按 `.slnx` 里的声明顺序 ——
否则 `Andzip.App` 明明是文件里第一行，不该被跳过。`n` < `s`，所以 `native/csharp/Andzip.Native`
插到了队首，把启动项目占成了类库。

修法不是去教用户点右键，而是**让字典序第一项必须是主程序**：把两个 C# 外壳工程
`Andzip.Native`、`Andzip.Native.Tests` 从 `native/csharp/` 移到 `src/` 下，和其它 C# 工程放一起。
移动后要改的引用一共 6 处：`Andzip.slnx`、`Andzip.Core.csproj` 的 `ProjectReference`、
`build.ps1`、`run.ps1`（两处）、`native/scripts/build-windows.ps1` 的 DLL 暂存路径 —— 外壳自己的
`ProjectReference` 是相对路径，跟着一起移动后仍然正确。另外必须删掉目录里的 `.vs/`，
它里面那份 `.suo` 还记着 `Andzip.Native`，不删的话修好了规则也照样报错。

顺带说明：`native/` 仍然是完整的 C/C++ 内核（`include/`、`src/`、`cmake/`、`scripts/`、
`third_party/`、`tools/`、`tests/native`），只是不再夹带 .NET 工程；外壳是内核 ABI 的
**消费者**，和其它 C# 工程同层，这个归属本来就更合理。

验证：复制一份只留 `Andzip/` 的干净目录（34 MB，无任何构建产物）→ `dotnet build`、
`dotnet test`（178）、外壳自检（69）、`build.ps1` 打包、`run.ps1` 启动 —— 全部通过；
`Andzip.slnx` 里字典序第一项现在是 `src/Andzip.App/Andzip.App.csproj`。

### 13. Linux / macOS 上内核**永远加载不到**：产物文件名与外壳探测名不一致

写 CI 的 Linux 作业时顺手发现的。它一直藏在「本机跑不了 Linux 二进制」这句话后面。

- `native/CMakeLists.txt` 里的 `PREFIX ""` 是**无条件**设的，于是 Linux 产物叫
  `andzip_core.so`；
- 外壳 `NativeLibraryResolver.FileName` 在非 Windows 上探测的却是
  **`libandzip_core.so`** / `libandzip_core.dylib`；
- `build-unix.sh` 自己也按 `libandzip_core.so` 去找（用来打印内核版本），所以那段
  `azcore version` 在 Linux 上从来没执行过 —— 被 `[[ -f ]]` 静默跳过了。

三处两两不一致，后果是：**在 Linux / macOS 上 .NET 侧永远找不到引擎**，程序只会显示
「compression engine unavailable」。Windows 看不出任何问题，因为 MSVC 本来就不加前缀。
之前所有交叉编译验证都只做到「`.so` 编得出来、导出符号对」，从没检查它**叫什么名字** ——
这正是「只编译不执行」的代价，也是加 CI 的意义。

修法：`PREFIX ""` 只在 `WIN32` 下设置，其它平台保留 CMake 默认的 `lib` 前缀，三处对齐到
`libandzip_core.so` / `libandzip_core.dylib`。改完重新交叉编译三个目标验证：
`build/linux-x64/bin/libandzip_core.so` 与 `build/linux-arm64/bin/libandzip_core.so` 各 41 个
导出，静态库 `libandzip_core.a`（30.9 MB）被 `azcore` 成功链接。

### 14. 门禁脚本自己会「吞掉失败」并报告全绿

`verify-all-targets.ps1` 的每一步只检查 `$LASTEXITCODE`。但**可执行文件找不到**时抛的是
`CommandNotFoundException`，`$LASTEXITCODE` 根本不更新（还是 0）—— 于是这台机器上 `cmake`
不在 PATH（它装在 VS 里面）的情况下，Linux 三个目标整段没跑，脚本却以退出码 0 结束。
我第一版 README 里「一条命令跑完所有平台」那句话，就是被这个假绿骗出来的。

两处修：① `Invoke-NativeStep` / `Invoke-Gate` 一律 `try { 步骤 } catch { 带标签重新抛出 }`，
外部命令的非零退出与终止错误都算失败；② 脚本自己用 vswhere 定位 VS 自带的 CMake/Ninja 并加进
`PATH`，找不到就明确报错，而不是假装跑过。修完 Linux 三个目标才第一次真正跑通（见第 13 条）。

同类问题还有一处，是 `ci.ps1` 自己的：`& build-windows.ps1` 是**同进程**调用，它导入的 vcvars
环境会漏进 `ci.ps1`，其中 `Platform=X64` 被 MSBuild 当成解决方案平台，于是后面的
`dotnet build` 报 `MSB4126: The specified solution configuration "Release|x64" is invalid`。
改成用子进程跑引擎脚本，并在开头清掉继承来的 `Platform`（解决方案只有 Any CPU）。
这个坑是**本地跑第一次 `ci.ps1` 时暴露的**，不是靠读代码读出来的 —— 门禁脚本本身也需要门禁。

---

## 八、下一步建议（按性价比排序）

1. **把「新建压缩包」对话框的口令框在 ZIP + 非 ASCII 时直接置灰**，而不是等到点开始才报错。
   小改动，避免用户撞墙。
2. **压缩包视图升级成真正的树形 + 工具条 + 右键菜单**（FR-4 的后续切片）。现在能读 60 种
   格式了，但界面还配不上这个能力。
3. **接入解压中途的口令回调**：内核已支持 `az_password_fn`，Core 的 `ExtractionSettings`
   加一个 `Func<string, string?>` 即可，能省掉「先探测再输一次口令」这一步。
4. **实现 7z 原生分卷输出**（内核侧 `CMultiOutStream` + 逐卷进度），替换掉只有 ZIP 能分卷
   的现状。做之前先读 `native/README.md` 里「报告成功但一个文件都不生成」那段教训。
5. **异常文本本地化**：给内核消息配一层「错误类别 → 语言键」的映射，界面不再直接显示英文。
6. **补 `az_item_meta` 的条目注释通道**并验证 7-Zip ZIP 侧那条窄路径是否真能用；不能用就
   明确地把这个功能从界面移除，别留半截。
7. **在真 Linux 机器上跑 `andzip_core_tests` 与 `dotnet test`**，把第六节第 16 条消掉。
   顺手加上 `-DCMAKE_CXX_FLAGS=-fsanitize=address,undefined`（clang/gcc 都行）：第七节第 10 条
   那类「对未初始化内存调释放函数」的问题，ASan/UBSan 在 Linux 上一次就能查出来，而本机
   MSVC + 非管理员权限既开不了 PageHeap 也拿不到带符号的崩溃现场，定位成本高了不止一个数量级。
8. ~~加 CI~~ —— **已完成**：`ci.ps1` 是门禁本体（本地和 CI 跑同一份），
   `.github/workflows/ci.yml` 在 Windows 上调 `ci.ps1 -Static`，并新增 Linux / macOS 两个
   **真机执行内核**的作业（这是本机一直做不到的那一条，见第七节第 13、14 条）。
   控制台自检 `Andzip.Native.Tests` 由 `build.ps1` 显式跑，不依赖 `dotnet test` 发现它。
   剩下要做的只有：`git init` + 推到远端，让 workflow 真的被触发（见第四节第 7 步）。
9. **把 `native/scripts/gen-sources.mjs` 换成 PowerShell**：它需要 Node，而这台机器上
   根本没有 Node（本次是靠内置 Node 跑的）。清单已入库所以不影响构建，但重新生成时会卡住。

---

## 九、验证记录

### 自动化

| 套件 | 数量 | 结果 |
|---|---|---|
| `native/tests/native/test_main.cpp`（动态库） | 532 条断言 | ✅ 0 失败 |
| 同一套件，静态库 `/WHOLEARCHIVE` 链接 | 532 条断言 | ✅ 0 失败 |
| `tests/Andzip.Core.Tests`（xUnit） | 209 个用例 | ✅ 0 失败，**连续 5 次运行稳定** |
| `src/Andzip.Native.Tests`（控制台自检） | 69 条断言 | ✅ 0 失败 |
| `native/scripts/check-exports.ps1` | 41 个导出 | ✅ win-x64 `.dll`、linux-x86_64 `.so`、linux-aarch64 `.so` 三侧一致 |
| 定向压力：CLR 宿主里跑压缩往返 | 5 组配置 × 6 次 × 1500 | ✅ 0 崩溃（修复前约每 1500 次崩 1 次） |
| 常驻回归守卫：外壳自检里的 `writer stress` | 4000 次加密 ZIP 写入 | ✅ 0 失败；把 bug 改回去后 **6 次里崩 5 次** |

`writer stress` 是这次新增的一条常驻检查（`Andzip.Native.Tests` 里，整个套件因此从 68 条变成
69 条，多花约 3 秒）。它的价值验证方式是**反向做一次**：把 `PropEntry` 的默认构造删掉重新编译
内核，4000 次写入的循环 6 次里崩 5 次；恢复修复后 6 次全绿。这类未初始化内存的缺陷本来就是
概率性的，所以检查的目标不是「一定抓到」，而是「CI 上大概率抓到」。

`dotnet test Andzip.slnx` **只覆盖 xUnit 那一套**：`Andzip.Native.Tests` 是没有测试适配器的
控制台程序，对它 `dotnet test` 只会还原、不会运行。`build.ps1` 现在显式把它跑一遍并把非零
退出当失败，否则「测试全绿」这句话会漏掉 69 条 ABI/句柄生命周期检查。

构建 0 警告；`build.ps1` 全流水线（版本号 → 测试门禁 → 发布 → 字节扫描确认内核已内嵌 → 体积）
退出码 0，产物 `dist/Andzip-v0.9.1-win-x64.exe` **19.49 MB**（预算 25 MB）。

Core 测试的覆盖面：ABI 协商、格式/编解码器表、内存与目录往返、三种口令方案、进度与取消、
路径穿越、非法文件名清洗、智能解压、分卷命名与拼接、GZ/TGZ 约束、语言包解析与键覆盖、
菜单模型与快捷键、版本号校验、**压缩包目录树重建**（`ArchiveHierarchy`：无目录条目、
退化斜杠写法、目录行取真实元数据、排序规则）、**按清单序号回读条目字节**
（`ArchiveContentSession`，缩略图的地基）。测试里仍引用 SharpZipLib，但**只在测试工程**，作用是
「用另一个独立实现读回我们的产物」—— 自产自验会掩盖对称性缺陷。

### 只保留 `Andzip/` 的独立验证

要求是「该文件夹外的文件都可以删除后仍能正常运行」，所以把整个目录**原样复制**到
`az-solo\Andzip`，并删掉可再生产物（`build/`、`dist/`、所有 `bin/`、`obj/`、`.vs/`），
只剩 34 MB 源码 + 一个已暂存的 `andzip_core.dll`，然后在那个目录里依次执行：

| 步骤 | 结果 |
|---|---|
| `dotnet build Andzip.slnx -c Release` | ✅ 成功，0 警告 0 错误，4.7 秒（**不需要 CMake、不需要 C++ 工作负载**） |
| `dotnet test Andzip.slnx -c Release` | ✅ 187 / 187 |
| `dotnet run --project src/Andzip.Native.Tests` | ✅ 69 条断言 0 失败（含 4000 次写入压力） |
| `powershell -File build.ps1` | ✅ 全门禁通过，产出 `dist/Andzip-v0.9.0-win-x64.exe` 19.47 MB，字节扫描确认内核已内嵌 |
| `powershell -File run.ps1` | ✅ 启动主程序，截图核对首页：菜单栏 + 蓝色工具栏 + 空白双栏 |

审计过的对外依赖只剩一处：`native/scripts/vendor-sources.ps1` 需要上游原始压缩包
（`cankao/`，约 700 MB），它只在**重新 vendor 第三方源码**时才用，构建、测试、运行、打包都不碰它；
脚本现在会在两处探测并在找不到时提示用 `-SourceArchives` 指定。出处记录在
`native/third_party/MANIFEST.json`（在本目录内），所以许可证审计也不依赖外部文件。

### 门禁（`ci.ps1 -Static`）在本机的完整一次

| 步骤 | 结果 |
|---|---|
| 1 引擎动态库 + 原生断言 | ✅ 532 checks, 0 failures |
| 2 导出表核对 | ✅ exported 41 |
| 3 引擎静态库 + 原生断言 | ✅ 532 checks, 0 failures |
| 4 托管解决方案构建 | ✅ 0 警告 0 错误 |
| 5 打包（内含两套测试） | ✅ 187 / 187，69 checks 0 failures，内核已内嵌，19.47 MB |

同一轮还跑通了 Linux 交叉三目标：`libandzip_core.so`（x86_64 / aarch64，各 41 导出）与
`libandzip_core.a`（30.9 MB，被 `azcore` 成功链接）。

> 这一步本身修掉了两个「假绿」：门禁脚本只查 `$LASTEXITCODE` 导致 Linux 整段没跑仍报成功，
> 以及 `ci.ps1` 同进程调用引擎脚本导致 `Platform=X64` 泄漏进 `dotnet build`。
> 见第七节第 14 条。

### 手动

- **单文件绿色版**：直接启动 `dist/Andzip-v0.9.1-win-x64.exe`（`FileVersion 0.9.1.0`、
  `ProductVersion 0.9.1+10`），主窗口存活，标题栏
  `Andzip (Professional) · 7-Zip 26.04 (x64) · Release · 60 formats / 7 writable`，随后正常关闭。
- **内核命令行往返**（`build/native/bin/azcore.exe`，同一个 DLL）：把含中文与空格的目录
  压成 ZIP + AES-256 口令包 → 列表显示 5 个条目（3 文件 + 2 目录，名字里的中文没有变成乱码）→
  解压 → 逐个文件比 SHA-256，**3/3 完全一致**。
- **界面逐屏截图核对**（`Andzip.exe` + 一个 402 文件 / 4 目录、965 MB 的真实照片包）：
  ① 首页 = 菜单 + 工具栏 + 空白双栏；② 命令行传包 → 标题变成
  `集训队证件照.zip - Andzip (Professional)`，树显示 `集训队证件照.zip → 集训队证件照 → 白/白 保障卡/红/蓝`，
  11 列表头与参考软件逐字一致，状态栏 `文件: 402, 文件夹: 4, 压缩文件大小: 965.7 MB`；
  ③ 点树进子目录 → 首行 `..`，文件行显示 `JPG 文件` / `Deflate` / CRC 十六进制 / `A__` /
  `OS:Windows`；④ 切图标视图 → 100 张证件照缩略图全部解出，`..` 以文件夹图块出现。
  工具栏「打开」→ 系统文件框标题「打开压缩文件」，类型下拉只有
  「压缩文件 (*.001;*.7z;…)」一项，目录里非压缩包的文件不可选。
- `build/native/bin/azgui.exe`（内核自带的最小验证界面，**不是主程序**；源码在
  `native/tools/azgui/`）：自检 14/14；压缩目录
  243,764 → 201,896 字节并逐字节往返校验 0 处不符；列出 5 个条目；解压后在界面之外用
  SHA-256 复核，3 个文件全部一致。（这一轮是在 `PROPVARIANT` 修复**之前**做的；修复之后
  重跑的是上面的 azcore 往返与绿色版启动，两者走的是同一套写入代码。）

### 建议你手动过一遍的清单

1. 新建 → 选一个含中文文件名的文件夹 → 格式 **7Z** + 中文密码 → 开始 → 观察进度条百分比。
2. 打开第 1 步的包 → 确认列表正常 → 解压到已有文件的目录 → 触发「重名询问」→ 选自动重命名。
3. 新建 → 格式 **ZIP** + 中文密码 → 应看到明确的「ZIP 密码仅支持 ASCII」提示，而不是英文报错。
4. 新建 → ZIP + 分卷 3MB → 压一个 10 MB 的文件 → 确认 `name.z01`、`name.zip` 生成，
   且能整体解压回去。
5. 打开 `.tar.gz` / `.tgz` → 确认列表显示的是内层 tar 的内容而不是一个成员文件。
6. F5 打开设置 → 文件关联页 → 勾一个平时没关联的格式（比如 LZMA）→ 立即应用 →
   资源管理器里右键该文件 → 「打开方式」里应出现 Andzip；再取消勾选 → 立即应用 → 应当消失。
7. 设置里点「重置」→ 勾选应回到出厂集合（ZIP / 7Z / RAR / TAR / GZ… 勾上，EXE / IMG / JAR 不勾）；
   点「确定」关窗后重开 F5，勾选状态应当保持（存在 `%APPDATA%\Andzip\settings.ini`）。
6. 打开一个 `archive.7z.001` 分卷包 → 确认能作为单个归档列出。

---

## 十、维护约定

- **每次改动都要同步更新本文件**：功能增删、限制变化、测试结果、构建步骤，改代码的同一个
  提交里改掉对应描述。过期的「未验证」「未实现」比没有文档更有害。
- 本文档与 `native/README.md` 用中文书写。注释分区：**`native/` 下的 C/C++ 用英文并且
  整个目录保持纯 ASCII**（内核工程没有加 `/utf-8`，源码里出现中文会触发 `C4819`，而编译器
  按本地代码页 936 读 UTF-8 字节是实打实的风险）；`src/` 下的 C# 沿用原工程风格（中文注释）。
- 改完跑一条命令就够：**`powershell -ExecutionPolicy Bypass -File ci.ps1 -Static`**。它就是
  CI 的 Windows 作业跑的那一条，包含引擎构建、532 原生断言、导出表核对、静态库、解决方案
  构建、两套托管测试与打包。想跳过慢的部分：`-SkipEngine`（引擎没动时）、`-NoPackage`（不出
  绿色版时）。
- **门禁脚本必须能真的失败。** 只查 `$LASTEXITCODE` 是不够的：可执行文件找不到时抛的是
  终止错误而退出码不变，脚本会假绿（第七节第 14 条就是这么骗过一次）。新增步骤一律走
  `Invoke-Gate` / `Invoke-NativeStep`，它们同时捕获终止错误与非零退出并带标签抛出。
- CI 里 Linux 的托管测试是 `continue-on-error`，那是**故意**的：它从没在非 Windows 上跑过。
  要升格成硬门禁，先去看它报了什么，而不是直接把开关去掉。
- 改了 `native/` 的接口或构建，请同时更新 `native/README.md`（内核层的细节在那边）。
- 动过内核里跟 COM/`PROPVARIANT` 打交道代码后，除了测试套件，建议在 CLR 宿主里跑一轮上千次
  的写入循环 —— 这类未初始化内存的问题只在宿主内存被复用过的形状下才暴露（第七节第 10 条）。

---

## 十一、许可证与出处

`native/` 合并了多个上游，`native/third_party/MANIFEST.json` 是机器可读的来源记录。

| 组件 | 来源 | 许可证 |
|---|---|---|
| 7-Zip 引擎（`C/`、`CPP/`） | `cankao/7zip-main.zip` | LGPL-2.1-or-later **附 unRAR 限制** |
| zstd / lz4 / brotli / lizard 等 | `cankao/7-Zip-zstd-master.zip`、`cankao/NanaZip-main.zip` | BSD-3-Clause / MIT / BSD-2-Clause 等 |
| 上述组件的 7-Zip 封装 | `cankao/NanaZip-main.zip` | MIT |
| Zig（仅用于交叉编译，不进产物） | ziglang.org | MIT |
| SharpZipLib（**仅测试工程引用**，生产代码不依赖） | NuGet 1.4.2 | MIT |

发布前必须复核两件事：

1. **unRAR 限制**：可以解码 RAR，但不得据此支持 RAR 创建。要宣传什么 RAR 能力，先对照条款。
2. **LGPL 路线**：`andzip_core` 以 DLL 形式动态链接，走的是动态链接这条路；若改成静态链接
   发布，需要按 LGPL 提供相应便利。

`src/`、`tests/`、`native/src`、`native/include`、`native/tools`、`native/tests` 下的内容
都是本项目的原创工作，沿用 Andzip 自身的许可证。

> 注意：`cankao/`（上游原始压缩包）和 `Bandizip-RE/`、`Bandizip/`（逆向参考语料）都在
> 本目录**之外**。重新 vendor 需要 `cankao/`；日常构建不需要。`vendor-sources.ps1` 会在
> `Andzip/cankao` 与上一级 `cankao/` 两处探测，都找不到就报错并提示用
> `-SourceArchives <目录>` 指定；若你要把整个仓库收敛到 `Andzip/`，请先把 `cankao/`
> 留在能取到的地方，或删除前确认永远不再重新 vendor。
