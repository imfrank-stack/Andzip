# Andzip

Bandizip 风格的 Windows 压缩工具。**界面是 C#（.NET 10 + Avalonia，控件全部用代码构建，没有 XAML），
压缩/解压内核是 C/C++**（原样 vendor 的 7-Zip，外面包一层只有 43 个函数的扁平 C ABI；
5.7.0 起 ZIP 与 7z 的 Deflate 编码器换成了同样原样 vendor 的 zlib-ng，解码那一路仍是 7-Zip 自己的）。

一个二进制两种用法：免安装的 `dist/Andzip-v<版本>-win-x64.exe`，和双击就装的
`dist/Andzip-Setup-v<版本>-win-x64.exe`。各约 21 MB。

- **能做什么 / 做不到什么**：逐条带版本地写在
  [`docs/开发记录.md`](docs/开发记录.md) 第五节（已完成）与第六节（已知限制）。
- **两条不做的事**：不联网、不上传用户内容；不伪造 Windows 的默认应用保护哈希
  （`FileExts\<扩展名>\UserChoice`）—— 那是防第三方抢默认应用的机制，绕开它属于破解系统安全。
- **改界面先读** [`docs/界面设计规范.md`](docs/界面设计规范.md)；逐版本变更读
  [`CHANGELOG.md`](CHANGELOG.md)。

---

## 一、目录结构

```
Andzip/
├── README.md                   本文件
├── docs/开发记录.md             架构决策、踩坑定位、逐版本验证记录、能力与限制清单
├── docs/界面设计规范.md         色板 / 圆角 / 间距 / 字号 / 组件 / 图标的唯一依据
├── CHANGELOG.md                逐版本变更记录
├── VersionNo.ini               Version + BuildNo，发布时注入程序集版本
├── Andzip.slnx                 5 个 C# 工程：App / Core / Native / 两套测试
├── build.ps1                   版本号 → 测试（硬门禁）→ 发布 → 内嵌核对 → 两件产物 → 对产物跑 /selftest
├── ci.ps1                      CI 门禁本体：本地和流水线跑同一份
├── run.ps1                     不开 IDE 也能构建并启动主程序
├── langs/*.lang                语言包（English / SimpChinese，494 个键，键集一致）
├── assets/                     程序图标的图源（512×512 带透明）+ 裁出 .ico 的脚本
├── src/
│   ├── Andzip.App/             Avalonia 界面（MainWindow / App 启动分发 / Services 平台集成 / Views 各窗口）
│   ├── Andzip.Core/            业务逻辑：压缩解压门面、命名与重名、路径安全、分卷、语言包、设置存储
│   ├── Andzip.Native/          P/Invoke 外壳（43 个 az_* 函数的 C# 映射）
│   └── Andzip.Native.Tests/    外壳的托管断言（控制台自检程序）
├── tests/Andzip.Core.Tests/    xUnit
├── tests/perf/                 压缩性能测量脚本：读数落进 docs/perf-*.tsv，档位实际下发的东西由 tier-check.cs 逐字节核对
├── tests/decoder-compat.ps1    换编码器那道门槛：产物交给 7-Zip / Python / 资源管理器读回，逐字节比对
├── native/                     压缩内核（C/C++，细节见 native/README.md）
│   ├── include/andzip/andzip.h 唯一公开头文件：扁平 C ABI
│   ├── src/api/                C ABI 门面
│   ├── third_party/7zip/       原样 vendor 的 7-Zip，未修改一行
│   ├── third_party/codecs/     同样未修改一行的第三方编码器（zstd / lz4 / brotli / zlib-ng …）
│   ├── scripts/                vendor / 各平台构建 / 导出符号核对
│   └── tools/ + tests/native/  命令行工具与原生测试
├── build/                      CMake 输出（全部可再生，不入库）
└── dist/                       build.ps1 的产物目录
```

---

## 二、架构一眼版

分层是**单向**的，界面永远看不到 native 的类型：

```
  Andzip.App          Avalonia 界面            只调用 Core 的门面与选项记录
  Andzip.Core         深模块                   决定「事情怎么做」：命名、重名、路径安全、分卷、进度
  Andzip.Native       P/Invoke 外壳            43 个 az_* 函数，UTF-8，__cdecl，不透明句柄
  andzip_core.dll     7-Zip 引擎               决定「字节怎么变」：格式识别、编解码、CRC、固实块、多卷
```

两个判断值得单说：**接缝放在 Core 的四个静态门面上**（`ArchiveCreator` / `ArchiveExtractor` /
`ArchiveReader` / `ArchiveTester`），换引擎时界面一行都不用改——本项目实际上就这么做过一次；
**错误模型是翻译而不是透传**（`NativeEngine.Translate`），P/Invoke 的错误码不会成为界面必须知道的契约。

完整推导在 [`docs/开发记录.md`](docs/开发记录.md) 第三、七节。

---

## 三、怎么构建

前置：Visual Studio 2022/2026 的 **C++ 工作负载**（CMake / Ninja 用 VS 自带的）、**.NET 10 SDK**；
交叉编译 Linux 时另需 Zig（`native/scripts/provision-toolchain.ps1` 会下载便携版）。
Windows 上用 PowerShell；在 Git Bash 里请写成 `powershell -ExecutionPolicy Bypass -File …`。

### 1. 内核

```powershell
pwsh native/scripts/build-windows.ps1 -RunTests
```

编译 `andzip_core.dll`（约 285 个编译单元，几分钟）、跑原生断言、核对导出符号恰好是头文件声明的
43 个 `az_*`，最后**把 DLL 暂存到 `src/Andzip.Native/runtimes/win-x64/native/`**。
最后一步是界面工程能找到引擎的前提：**跳过它 `dotnet build` 照样成功**，只是程序一起来就显示
「compression engine unavailable」。

**要复现产品那一份（以及 `docs/` 里所有性能读数）得带两个开关**：
`pwsh native/scripts/build-windows.ps1 -ExtraCodecs -DeflateZlibNg -RunTests` —— `ci.ps1` 的两条引擎
作业显式带着它们。不带 `-ExtraCodecs` 时五个编码器根本不登记（外壳自检会失败并提示）；
不带 `-DeflateZlibNg` 时 Deflate 走的是 7-Zip 自带的 coder，**压出来的字节与那张档位表不是同一份**。

### 2. 界面与业务层，以及测试

```powershell
dotnet build Andzip.slnx -c Release
dotnet test Andzip.slnx -c Release                      # xUnit：Core + 界面
dotnet run --project src/Andzip.Native.Tests -c Release # 控制台自检，必须单独跑
```

第二条不能省：`Andzip.Native.Tests` 是不带 xUnit 适配器的控制台程序，`dotnet test` 对它只会还原、
不会运行。界面那套（`Andzip.App.UI.Tests`）用 Avalonia 的 headless 平台装配**真实的窗口**去断言
控件在不在、可不可用、点下去设置有没有落盘，另外会把主窗口与对话框渲染成 PNG 供人眼核对。
一个必须知道的边界：**headless 不检查线程亲和性**，从工作线程改控件在这里不会抛——
所以跨线程的进度上报要断言"回调落在哪个线程"，而不是"有没有炸"。

### 3. 跑起来

```powershell
pwsh run.ps1          # 构建 + 启动主程序
pwsh run.ps1 -Check   # 只构建 + 跑两套测试，不开窗口
```

标题栏应当显示 `Andzip (Professional) · 7-Zip 26.04 (x64) · Release · 60 formats / 7 writable`；
显示 `compression engine unavailable` 就是第 1 步没做或没暂存成功。

> 仓库里有好几个 exe，别搞混：**主程序**是 `dist/Andzip-v<版本>-win-x64.exe`（发布版）或
> `src/Andzip.App/bin/Release/net10.0/Andzip.exe`（开发版）；`azgui.exe` 是内核自带的最小验证界面、
> `azcore.exe` 是命令行工具、`Andzip.Native.Tests.exe` 是自检——三个都不是主程序。

### 4. 打包（绿色版 + 安装器两件）

```powershell
pwsh build.ps1
```

产物在 `dist/`：`Andzip-v<版本>-win-x64.exe`（下载即用）与 `Andzip-Setup-v<版本>-win-x64.exe`
（双击出安装向导）。**安装器不是第二个程序，是同一个文件改了名**（按自己的文件名判断身份），
所以安装器与应用不可能版本不一致，而装进电脑里的那份与绿色版是同一个字节流。
安装只做五件事：复制自己（默认 `%LOCALAPPDATA%\Programs\Andzip`，向导里可改）、留一个
360 字节的 `uninstall.bat`、建桌面与开始菜单的 `.lnk`、写卸载表、按勾选登记关联与右键菜单。
「为所有用户」把同样的几件事落到 `Program Files` + `HKLM`，为此把自己以管理员身份重启一次；
**提权被拒就停在当前那一页说明白**，不会悄悄降级成只给自己装。
卸载没有单独的 `uninstall.exe`：真卸逻辑在 `Andzip.exe /uninstall` 里，
独立的 exe 要么是那 20 MB 的第二份拷贝、要么只是个壳，而壳用 90 字节的批处理就够了。

**门禁不止跑测试**：单元测试跑的是未裁剪的 `Andzip.dll`，发出去的却是单文件 + 裁剪 + 压缩过的
`Andzip.exe`，而有些故障只活在这条差别里（1.8.3 那次安装器建不出快捷方式：1101 条断言全绿，
产物是坏的）。所以 `build.ps1` 会扫描产物字节确认引擎 DLL 真的在里面，最后一步**直接执行产物**：
`Andzip.exe /selftest` 在临时目录 + 探针注册表键里真装一次、真卸一次，逐条打 `ok` / `FAIL`，
非零退出就拒绝出包。之后还有一步真屏幕核对（`tests/gui-probe.ps1`：真的启动 Setup、真的把鼠标
点到屏幕坐标上、真的截那块像素）。这两步从 1.8.4 起是固定动作，不再靠"想起来才做一次"。

### 5. 一次跑完所有平台（可选）

```powershell
pwsh native/scripts/verify-all-targets.ps1                    # Windows 动态 + 静态 + Linux 三个目标
pwsh native/scripts/verify-all-targets.ps1 -DeflateZlibNg     # 同一个开关带着所有目标过一遍（产品那份）
pwsh ci.ps1 -Static                                           # 本地等价于 CI 的 Windows 作业
```

---

## 四、想贡献

- 改代码的**同一个提交**里把 `docs/开发记录.md` 的对应描述、`CHANGELOG.md`、`VersionNo.ini`
  一起更新；过期的「未实现」「未验证」比没有文档更有害。
- 改完至少跑 `pwsh ci.ps1 -Static`。新增门禁步骤请走脚本里的 `Invoke-Gate` / `Invoke-NativeStep`：
  只查 `$LASTEXITCODE` 会假绿（可执行文件找不到时抛的是终止错误而退出码不变，这事真的发生过）。
- 动过 `native/` 的接口或构建，同时更新 `native/README.md`。
- 关于压缩快慢的话只能来自重跑得出来的数：脚本在 `tests/perf/`，原始读数在 `docs/perf-*.tsv`；
  改「档位实际发出去的东西」（`NativeEngine.TierExtras`）要跑 `dotnet run tests/perf/tier-check.cs`
  把产物字节核对一遍，别留"快很多"这种没有出处的形容词。
  说的不是"多快"而是"吃了几核"的时候，用 `tests/perf/mt.ps1`：它每格除了秒数还记
  `cores_used = CPU 秒 ÷ 墙钟秒`（表落 `docs/perf-mt.tsv`）—— "单线程"这种话只能由这个数说，
  不能由任务管理器看一眼说。
  要比**别人的编码器**（换 deflate 这类）用 `native/tests/probe/` 那两个脚本：库 clone 到 `build/` 下、
  仓库不留副本、产品构建一行不碰 —— 实验不许有一条能写进仓库的路。
  **决定要换编码器是另一件事**，那一版走的是这条路：库进 `native/third_party/codecs/` 并在
  `MANIFEST.json` 记来源与许可证（一行不改上游文件，开关默认 `OFF`），然后 `pwsh tests/decoder-compat.ps1`
  把产物交给**自带的 inflate 与我们无亲无故的那几家**逐个读回来比对（表落 `docs/decoder-compat.tsv`）。
  这条不是装饰：换编码器那一次带出一条少写 16 472 字节的 bug，而当时内部断言全绿、包还变小了 ——
  它看起来就是一次提速（`docs/perf-p2c-deflate-swap.md` 第 5 节）。
- GUI 目前依赖人工点击，手动清单在 [`docs/开发记录.md` 第九节](docs/开发记录.md)；欢迎把它自动化。

---

## 五、许可证与出处

`native/` 合并了多个上游，机器可读的来源记录在 `native/third_party/MANIFEST.json`。

| 组件 | 许可证 |
|---|---|
| 7-Zip 引擎（`C/`、`CPP/`，原样 vendor，未修改一行） | LGPL-2.1-or-later **附 unRAR 限制** |
| zstd / lz4 / brotli / lizard 等 | BSD-3-Clause / MIT / BSD-2-Clause 等 |
| zlib-ng（`third_party/codecs/zlib-ng`，5.7.0 起 ZIP 与 7z 的 Deflate 编码器，同样未修改一行；`ANDZIP_DEFLATE_ZLIBNG=OFF` 的构建里它根本不进产物） | zlib License（BSD-style） |
| 上述组件的 7-Zip 封装 | MIT |
| Zig（仅用于交叉编译，不进产物） | MIT |
| SharpZipLib（**只有测试工程引用**，生产代码不依赖） | MIT |

`src/`、`tests/` 以及 `native/src`、`native/include`、`native/tools`、`native/tests`
是本项目的原创工作，沿用 Andzip 自身的许可证。

发布前必须复核两件事：**unRAR 限制**（可以解码 RAR，但不得据此支持 RAR 创建，要宣传 RAR 能力
先对照条款）与 **LGPL 路线**（`andzip_core` 以 DLL 形式动态链接；改成静态链接发布需要按 LGPL
提供相应便利）。

> 界面与产品设计参考 Bandizip。上游原始压缩包（`cankao/`）与逆向参考语料（`Bandizip-RE/`）
> 都**不在本目录内**：日常构建不需要它们，只有重新 vendor 内核时才需要 `cankao/`。
