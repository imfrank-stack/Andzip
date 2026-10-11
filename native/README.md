# native/ — Andzip 压缩内核

跨平台的 C/C++ 压缩引擎（`andzip_core`）+ C# 外壳（`Andzip.Native`）。它的目标是替
换 `src/Andzip.Core` 里基于 `System.IO.Compression` 和 SharpZipLib 的托管实现，改用
7-Zip 引擎——也就是 Bandizip、NanaZip 实际使用的那套代码——并通过一层扁平 C ABI 调用。

本目录是**纯增量的**。`src/`、`tests/`、`Bandizip-RE/` 既没有被引用也没有被修改，现有
解决方案的编译结果与之前完全一致。这里的东西还没有接入 App，正式采用是后续单独的一步
（见 [接入 Andzip.Core](#接入-andzipcore)）。

---

## 目录结构

```
native/
├── CMakeLists.txt              构建脚本；产物 andzip_core（+ azcore、tests、azgui）
├── include/andzip/andzip.h     唯一公开头文件：扁平 C ABI
├── src/api/                    约 3700 行：7-Zip 之上的 C ABI 门面
│   ├── az_pch.h                按路径包含 7-Zip 头文件；补齐 POSIX 缺失的少量声明
│   ├── az_internal.h           内部共享声明
│   ├── az_error.cpp            线程本地错误报告、HRESULT 映射
│   ├── az_util.cpp             UTF-8 / 路径 / 时间转换、进度、口令
│   ├── az_formats.cpp          格式与编码器注册表、handler 构造
│   ├── az_streams.cpp          CMemOutStream、CSinkOutStream
│   ├── az_reader.cpp           打开 / 探测 / 列举 / 解压
│   ├── az_writer.cpp           创建归档（IArchiveUpdateCallback）
│   ├── az_hash.cpp             哈希器
│   └── az_exports.cpp          构建信息、线程数与内存上限
├── cmake/
│   ├── 7zip-sources.cmake      生成文件：284 个引擎编译单元
│   ├── codecs.cmake            可选的额外编码器（默认关闭）
│   └── toolchain-zig-linux.cmake  用 Zig 交叉编译 Linux 的 CMake 工具链
├── scripts/
│   ├── vendor-sources.ps1      cankao/*.zip -> third_party/
│   ├── gen-sources.mjs         Arc.mak -> cmake/7zip-sources.cmake（需要 Node.js）
│   ├── build-windows.ps1       MSVC + Ninja（已验证）
│   ├── build-unix.sh           gcc/clang + Ninja 或 Makefiles
│   ├── provision-toolchain.ps1 下载便携版 Zig / CMake / Ninja（无需安装）
│   └── check-exports.ps1       校验产物只导出头文件里声明的 41 个 az_*
├── third_party/
│   ├── 7zip/                   原样 vendor 的 7-Zip：C/、CPP/、DOC/
│   ├── codecs/                 vendor 的 zstd、lz4、brotli、lizard、lz5 等
│   └── MANIFEST.json           每个条目的来源压缩包与许可证
├── tools/
│   ├── azcore/                 基于 ABI 的命令行工具：冒烟测试 + 用法示例
│   └── azgui/                  最小 Win32 GUI 验证程序（手动验证压缩/解压）
└── tests/native/               532 条断言的原生测试
```

C# 外壳（`Andzip.Native`）与它的自检程序**不在本目录**，而在 `../src/` 下：Visual Studio 在
解决方案没有 `.suo` 状态时按项目路径字典序选默认启动项目，`native/csharp/...` 会排在
`src/Andzip.App/...` 前面并把启动项目占成类库，F5 就报「无法直接启动带有『类库输出类型』的
项目」。外壳是内核 ABI 的消费者，与其它 C# 工程同层也更合理。本目录仍然是完整的 C/C++ 内核。

## 各层如何衔接

```
  C# (Andzip.Native) ──P/Invoke──┐
  azcore (CLI) / azgui (GUI) ────┤
                                 ▼
                    include/andzip/andzip.h   ← 对外只有 41 个 az_* 函数
                                 ▼
                         src/api/（门面）
                                 ▼
            7-Zip 引擎：CreateObject / IInArchive / IOutArchive / ICoder
                                 ▼
                         third_party/7zip
```

引擎是**逐字符原样** vendor 的，没有修改任何一处 7-Zip 源码。门面与引擎交互的方式和
`7z.exe` 完全相同——通过 `ArchiveExports.cpp`、`CodecExports.cpp` 在静态初始化阶段建立
的注册表。

---

## 构建

### Windows（已验证）

需要带 C++ 工作负载的 Visual Studio 2022/2026。CMake 和 Ninja 都用 VS 自带的，脚本会自
动找到。

```powershell
pwsh native/scripts/build-windows.ps1                    # Release x64
pwsh native/scripts/build-windows.ps1 -RunTests          # 构建后跑 532 条原生断言
pwsh native/scripts/build-windows.ps1 -Arch arm64
pwsh native/scripts/build-windows.ps1 -Config Debug -RunTests
pwsh native/scripts/build-windows.ps1 -ExtraCodecs       # 打开 zstd/lz4/brotli 编码器
pwsh native/scripts/build-windows.ps1 -Static -BuildRoot build/native-static -RunTests
pwsh native/scripts/build-windows.ps1 -NoGui -NoTests    # 只要库和 CLI
```

脚本会把 `vcvarsall.bat` 的环境导入当前进程，所以在普通 PowerShell 或 Git Bash 里都能
直接跑，不必开开发者命令提示符。构建完 DLL 之后它会自动执行
`scripts/check-exports.ps1`，导出符号集与头文件不一致就直接失败。

产物：`build/native/bin/andzip_core.dll`、`azcore.exe`、`azgui.exe`、
`andzip_core_tests.exe`，并在 `bin/runtimes/win-x64/native/` 下暂存一份副本给 .NET 用。

完整构建要编译约 284 个引擎编译单元，耗时几分钟。

### 静态库（已验证）

```powershell
pwsh native/scripts/build-windows.ps1 -Static -BuildRoot build/native-static -RunTests
```

产物是 `build/native-static/lib/andzip_core.lib`，`azcore.exe`、`azgui.exe`、
`andzip_core_tests.exe` 都改为链接它，**532 条断言全部通过**。

链接静态库时有一个必须知道的坑：7-Zip 的归档 handler 与编解码器是靠
`ArchiveExports.cpp` / `CodecExports.cpp` 里的全局构造函数注册的，没有任何符号被外部引用，
所以静态链接器会把这些目标文件丢掉，库"能加载但注册表是空的"（`az_format_count()` 返回 0）。
CMake 里已经用 `/WHOLEARCHIVE` 处理，自己写工程时需要：

```
MSVC:      link.exe ... /WHOLEARCHIVE:andzip_core.lib
GCC/Clang: c++ ... -Wl,--whole-archive libandzip_core.a -Wl,--no-whole-archive -lstdc++ -lm
```

同时消费方要定义 `ANDZIP_CORE_STATIC`（CMake 目标已带），否则头文件会按 `dllimport` 声明函数。


### Linux（已交叉编译验证）

这台机器上没有 WSL 也没有 Docker，因此 Linux 侧用 **Zig 作为交叉编译驱动**：Zig 自带
clang 和各目标的 libc，一次下载就能编译 glibc / musl / arm64，不需要装任何系统级工具。

```powershell
# 1) 取便携版工具链（默认装到 %LOCALAPPDATA%\andzip\toolchains）
pwsh native/scripts/provision-toolchain.ps1

# 2) 交叉编译
$env:AZ_ZIG_EXE = "$env:LOCALAPPDATA\andzip\toolchains\zig\zig.exe"
cmake -S native -B build/linux-x64 -G Ninja -DCMAKE_BUILD_TYPE=Release `
      -DCMAKE_TOOLCHAIN_FILE=native/cmake/toolchain-zig-linux.cmake `
      -DAZ_ZIG_TARGET=x86_64-linux-gnu -DANDZIP_BUILD_TESTS=OFF
cmake --build build/linux-x64 --target andzip_core

# arm64 只需换三元组
-DAZ_ZIG_TARGET=aarch64-linux-gnu
```

`AZ_ZIG_TARGET` 可取 `x86_64-linux-gnu`、`aarch64-linux-gnu`、`x86_64-linux-musl`；
静态 libc 再加 `-DAZ_ZIG_LIBC=musl`。工具链文件里 `AZ_ZIG_EXE` **优先从环境变量读**，
因为 CMake 做编译器探测时创建的 `try_compile` 临时工程不会继承命令行上的 `-D` 缓存项。

在装有 gcc/clang 的 Linux 机器上，正常走本地工具链即可：

```bash
native/scripts/build-unix.sh -T
```

**验证到什么程度（诚实说明）**：

| 目标 | 编译 | 链接 | 导出符号核对 | 跑测试 |
|---|---|---|---|---|
| Windows x64 动态库 | ✅ MSVC 14.51 | ✅ | ✅ 41/41 | ✅ 532 断言 |
| Windows x64 静态库 | ✅ | ✅（azcore/azgui/tests 均链接它） | n/a | ✅ 532 断言 |
| Linux x86_64 动态库 | ✅ Zig/Clang 22 | ✅ 带 `--no-undefined` | ✅ 41/41 | ⚠️ 本机跑不了，交给 CI 的 `linux` 作业 |
| Linux aarch64 动态库 | ✅ | ✅ | ✅ 41/41 | ❌ |
| Linux x86_64 静态库 | ✅ | ✅（azcore 链接 `andzip_core.a`，30.9 MB） | n/a | ❌ |
| macOS | ❌ 无 Apple SDK | — | — | — |

这台机器上没有 WSL 也没有 Docker，所以 Linux 产物只能静态核对：`DT_NEEDED` 只有
`libc.so.6 / ld-linux / libpthread / libdl`（C++ 运行时由 Zig 静态链入），导出表恰好是头文件
里的 41 个 `az_*`。POSIX 分支这次是第一次被真正编译，修掉的问题列在
[已知限制](#已知限制) 里。**Linux 上的运行时行为仍未验证**，需要在一台真机上跑
`andzip_core_tests`。


### macOS

没有 macOS 编译器和 SDK，因此**未构建过**。代码路径与 Linux 共用 `#else` 分支，
`az_pch.h` 里也按 `CFiTime = timespec` 处理，但真机验证仍然缺失。

---

## 使用说明

### 1. 命令行：azcore

最快的验证方式，也是 ABI 用法的活教材。

```powershell
build\native\bin\azcore.exe formats                 # 支持的格式
build\native\bin\azcore.exe codecs                  # 支持的编解码器
build\native\bin\azcore.exe list    backup.7z       # 列出条目
build\native\bin\azcore.exe create  out.7z -f 7z -m LZMA2 -x 7 C:\some\dir
build\native\bin\azcore.exe extract out.7z C:\out -p hunter2
build\native\bin\azcore.exe test    out.7z          # 只校验不解压
build\native\bin\azcore.exe hash   file.bin -a sha256
```

### 2. 最小 GUI 验证程序：azgui

`native/tools/azgui` 是纯 Win32（无框架、无第三方 UI 库）的单窗口程序，用来手动确认压缩和
解压确实工作：

```
build\native\bin\azgui.exe
```

窗口分左右两块，下面是条目/结果表：

- **压缩**：选源文件或文件夹 → 选格式（下拉里是引擎报告可写的那些，7z/zip/tar/gzip/bzip2/xz/wim
  排在最前）→ 选方法与级别 → 可选口令与"加密文件名" → 开始压缩。"目标归档"留空会弹保存对话框。
  默认勾上 **压缩后逐字节往返校验**：压缩完成后把刚生成的归档解到临时目录，逐个文件比对字节。
- **解压**：选归档 → 列出条目（名称/方法/原始大小/压缩后/CRC/属性/修改时间）→ 选目标目录 →
  开始解压；勾选"只校验 CRC"则只验完整性、不落盘。
- 右下角三个按钮：**自检**（不碰用户文件，在内存里对 14 组"格式×方法×是否加密"做往返并逐字节
  比对）、**格式列表**、**编码器**。

进度回调可能来自编码器线程，所以它只 `PostMessage`，控件一律由窗口过程更新；任务运行期间按钮
被禁用，关窗口会先询问。

已经验证过的实际结果（`build/native/bin/azgui.exe`，通过发送 `BM_CLICK`/`WM_SETTEXT` 驱动真实
控件，再用屏幕外部的 SHA-256 复核）：

```
自检      passed 14 of 14        例：7z/LZMA2 262144 -> 1307，zip/Copy+AES 262144 -> 262350
压缩      243764 (3 file) -> 201896，checked 3 file(s), 0 mismatched
列出条目   5 entries（含 2 个目录项，方法 LZMA2:18，CRC 正确）
解压      目标目录内 3 个文件 SHA256 与源文件全部一致，mismatches: 0
```


### 3. C ABI

```c
#include "andzip/andzip.h"

az_create_options o = AZ_CREATE_OPTIONS_INIT;
o.format  = "7z";
o.method  = "LZMA2";
o.level   = 7;
o.dict_size_mib = 128;
o.password = "hunter2";
o.encryption = AZ_ENC_AES256;
o.encrypt_headers = 1;

az_writer w = NULL;
az_result r = az_writer_open("backup.7z", &o, &w);
if (r == AZ_OK) {
  az_writer_add_file(w, "C:\\project", "project", /*recurse=*/1);
  r = az_writer_finish(w);            // 真正开始压缩发生在这里
}
az_writer_release(w);
if (r != AZ_OK) puts(az_last_error_message());
```

### 4. C#

```csharp
using Andzip.Native;

// 读
using var archive = ArchiveReader.Open("backup.7z");
foreach (var entry in archive.Entries)
    Console.WriteLine($"{entry.Size,12} {entry.Method,-12} {entry.Name}");
archive.ExtractTo(@"C:\out", new ExtractOptions { Password = "hunter2" });

// 写
using var writer = ArchiveWriter.Create("backup.7z",
    new CreateOptions { Format = "7z", Method = "LZMA2", Level = 7 });
writer.AddFile(@"C:\project", entryName: "project", recurse: true);
writer.AddData(Encoding.UTF8.GetBytes("hello"), "readme.txt");
writer.Finish();
Console.WriteLine(writer.Warnings);   // 该格式无法满足的设置项
```

---

## 产物文件名（跨平台契约）

| 平台 | 动态库 | 静态库 | C# 外壳探测的名字 |
|---|---|---|---|
| Windows | `andzip_core.dll` | `andzip_core.lib` | `andzip_core.dll` |
| Linux | `libandzip_core.so` | `libandzip_core.a` | `libandzip_core.so` |
| macOS | `libandzip_core.dylib` | `libandzip_core.a` | `libandzip_core.dylib` |

`CMakeLists.txt` 里的 `PREFIX ""` **只能对 Windows 生效**：非 Windows 上清掉前缀会得到
`andzip_core.so`，而外壳按 POSIX 惯例探测 `libandzip_core.so`，结果是内核在 Linux/macOS 上
**永远加载不到**，而 Windows 侧一切正常、交叉编译也照样"通过"。这条以前就是错的，是加 CI 的
Linux 作业时发现的（见 `../README.md` 第七节第 13 条）。

## 支持的格式与编解码器

从构建好的库里实测（`azcore formats`、`azcore codecs`），默认配置
（`ANDZIP_EXTRA_CODECS=OFF`）：

- **可读容器格式 60 个**，其中 **可写 7 个**：`7z`、`zip`、`tar`、`gzip`、`bzip2`、
  `xz`、`wim`。
- **编解码器 25 个**，除 RAR 外均可编也可解：`LZMA2`、`LZMA`、`PPMD`、`BZip2`、
  `Deflate`、`Deflate64`、`Copy`、`Delta`、`BCJ`、`BCJ2`、`Swap2`、`Swap4`、`7zAES`、
  `AES256CBC`，以及 `Rar1/Rar2/Rar3` 解码器。
- 已注册 `ARM`、`ARM64`、`ARMT`、`IA64`、`PPC`、`SPARC`、`RISCV` 转换过滤器，所以
  7z/zip 里的可执行数据自动获得分支调用优化。
- **Zstandard**：解码可用（7-Zip 的 `C/ZstdDec.c` 加 `ZstdHandler`）。编码需要
  `-ExtraCodecs`，它会引入 7-Zip-zstd fork 的编码器。
- `rar`、`cab`、`iso`、`nsis`、`wim`、`chm`、`apfs`、`ext`、`fat`、`ntfs`、`udf`、
  `squashfs`、`dmg`、`vhd/vhdx`、`rpm`、`arj`、`lzh` 等其余格式随引擎免费提供。

指定方法与级别用的是引擎自己的属性名（`"m"`、`"x"`、`"d"`、`"mt"`、`"memuse"`、`"s"`、
`"he"`、`"em"`），和 `7z` 命令行发的完全一致，所以 CLI 能表达的东西都可以通过
`extra_properties`（换行分隔的 `name=value`）抵达。

---

## C ABI 约定

`include/andzip/andzip.h` 就是全部契约。设计规则：

- **纯 C99。** 没有 C++ 类型，不跨边界抛异常，两侧的 CRT 分配不互相释放。
- **每个结构体都带 `struct_size`。** 原生侧与 `sizeof` 比对，不一致就返回
  `AZ_ERROR_INVALID_ARG`，头文件过期会大声失败而不是悄悄踩内存。输出结构
  （`az_format_info`、`az_codec_info`、`az_entry_info`）同理，调用前先设好。
- **所有字符串都是 UTF-8。** 由库持有所有权，归属规则写在每个函数的注释里。凡是带
  `_bag` 字段的都要配对调用 `az_*_release`。
- **不透明句柄。** `az_archive`、`az_writer` 是指向不完全类型的指针。
- **`az_result` + `az_last_error_message()`。** 消息是线程本地的，在同线程下一次 `az_*`
  调用前有效。`AZ_OK` 且消息非空，表示"请求成功了，但某个设置被忽略了"——
  `az_writer_finish` 成功后要读这条消息并作为警告呈现。
- **统一 `__cdecl`**（`AZ_CALL`），托管侧 `CallingConvention.Cdecl` 在 x86 和 x64 上都正确。
- **`PROPVARIANT` 只能先 `PropVariantInit` 再用，不能先 `PropVariantClear`。** 这不是风格
  问题：`PropVariantClear` 会**读** `vt` 并按它分派释放（`SysFreeString` / `CoTaskMemFree` /
  `Release`），对未初始化的变量调用就是对一个野指针执行释放。7-Zip 的
  `CObjectVector<T>::AddNew()` 用的是 `new T`（默认初始化，POD 成员**不清零**），所以带
  `PROPVARIANT` 成员的结构必须自己写默认构造把它 Init 掉。这条踩过一次很贵的坑，详见
  [`../README.md` 第七节第 10 条](../README.md)。
- **只导出 41 个符号，别的一个没有。** `native/scripts/check-exports.ps1` 对 `.dll` 走
  dumpbin、对 `.so` 自己解析 `.dynsym`，两侧都核对过；期望值直接从 `andzip.h` 读取，所以
  不会和代码脱节。7-Zip 的 `STDAPI` 本身不带 `dllexport`，vendor 时也刻意丢掉了 `.def`
  文件，所以 COM 符号不会泄漏。

之所以是两个句柄而不是一个，是因为两个方向确实不同：`az_archive` 负责读，`az_writer` 负责
写。写入的流程是 open → 添加条目 → finish；引擎的 `UpdateItems` 要求事先知道条目总数，这就
是 `az_writer_add_file` 在调用时当场遍历目录树、而不是推迟到 finish 的原因。

---

## C# 外壳

`src/Andzip.Native` 面向 `net10.0` 封装了这套 ABI。原生库按
`runtimes/<rid>/native/`、`AppContext.BaseDirectory`、环境变量 `ANDZIP_CORE_PATH` 的顺序
解析，加载失败时会把尝试过的所有路径都报出来。

所有非 `AZ_OK` 都会变成 `AndzipException`，同时携带 `AzResult` 和原生消息，因此
`AzResult == AzResult.Password` 是真正的判断，不是字符串匹配。

`Andzip.Native.Tests` 是个普通控制台程序（不依赖 NuGet 测试包，可离线还原），直接驱动真实
的 DLL：内存往返、目录往返、三种口令方案、口令回调、进度、路径穿越防护、已知哈希值。
**注意它不会被 `dotnet test` 发现**（没有 xUnit 适配器，`dotnet test` 对它只还原不运行），
所以必须像上面那样显式 `dotnet run`；`../build.ps1` 的门禁里已经把这一条单独跑上了。

句柄所有权：`ArchiveWriter` / `ArchiveReader` 会**接管**创建它时传入的 `CreateOptions` /
`OpenOptions` 对应的原生块（含进度回调的委托与 `az_callbacks`），直到 `Dispose()` 才释放。
内核在 `az_writer_finish` / `az_archive_extract` 期间还要回调它。所以新增 API 时不要把
`NativeArgs` 写成 `using var`挂在打开方法上 —— 那是一枚 use-after-free。

---

## 接入 Andzip.Core（已完成）

内核现在就是 App 的实际压缩路径，接缝只有一个：`../src/Andzip.Core/Archive/NativeEngine.cs`。

1. `Andzip.Core.csproj` 引用 `src/Andzip.Native`，GUI 不直接引用它 ——
   对界面而言内核是 Core 的实现细节。
2. `ArchiveExtractor` / `ArchiveCreator` 重写在这层翻译之上：格式表、方法表、可写格式
   都从引擎运行时查出来（`az_format_count` / `az_codec_get`），不再在托管侧抄一份清单。
3. 没有保留托管回退开关。`System.IO.Compression` 那条路已经删干净，两条实现并存只会让
   「测的是 A、跑的是 B」。
4. 发布：App 打成单文件绿色程序，`andzip_core.dll` 作为**内容项**进入
   `runtimes/win-x64/native/`，因此必须用 `IncludeAllContentForSelfExtract=true`
   （`IncludeNativeLibrariesForSelfExtract` 只管 RID 资产，漏掉它会得到一个「压缩引擎不可用」
   的绿色版）。`../build.ps1` 在发布后直接扫产物字节确认 DLL 真在包里。
5. `SharpZipLib` 现在只在**测试工程**里，用途是「用另一个独立实现读回我们的产物」；
   Core 与 App 都不再依赖它。

许可证需要注意：7-Zip 是 LGPL-2.1-or-later **并附带 unRAR 限制**。把它链接进分发的 App
是可以的，但 unRAR 条款意味着不得用 RAR 解码器去支持 RAR 创建；LGPL 则要求动态链接，或者
一并发布组合作品的源码。`andzip_core` 以 DLL 形式构建，走的正是动态链接这条路。

---

## 验证与测试

```powershell
# 一条命令把所有平台过一遍（Windows 动态+静态并跑测试，Linux 两个三元组 + 静态并核对导出）
# 这条脚本自己也会用 vswhere 找 VS 自带的 cmake/ninja —— 它们不在 PATH 上。
pwsh native/scripts/verify-all-targets.ps1

# 单独跑其中一步：
pwsh native/scripts/build-windows.ps1 -RunTests          # 动态库：构建 + 导出核对 + 532 断言
pwsh native/scripts/build-windows.ps1 -Static -BuildRoot build/native-static -RunTests
dotnet run --project src/Andzip.Native.Tests   # 69 条托管断言（含 4000 次写入压力）
pwsh native/scripts/check-exports.ps1 build/native/bin/andzip_core.dll
pwsh native/scripts/check-exports.ps1 build/linux-x64/bin/libandzip_core.so
pwsh native/scripts/check-exports.ps1 build/linux-arm64/bin/libandzip_core.so
```

`verify-all-targets.ps1` 用 `&` 直接在自己这个 PowerShell 进程里调子脚本，不假设 `pwsh` 在
PATH 上 —— 这台机器只有 Windows PowerShell 5.1，早期版本写死 `pwsh` 时整条命令会静默停在第一
步。（`build.ps1` 同理：`powershell -ExecutionPolicy Bypass -File build.ps1` 和 `pwsh build.ps1`
都要能用。）

`check-exports.ps1` 对 `.dll` 走 dumpbin（用 vswhere 定位，不需要开发者命令行），对 `.so`
自己解析 ELF `.dynsym`；期望值直接从 `include/andzip/andzip.h` 读取，所以清单不会和代码脱节。

当前状态（上面那条 `verify-all-targets.ps1` 一次性跑出来的结果，`PROPVARIANT` 修复之后）：
**Windows x64 Release 下动态库与静态库各跑一遍 532 条原生断言，全部通过、0 失败、编译无警告**
（静态库用 `/WHOLEARCHIVE` 链接，注册表全局构造不会被裁掉；`azcore` / `azgui` / 测试程序都链
它）；托管侧另有 69 条断言。**Linux x86_64 / arm64 动态库重新交叉编译并核对过导出**（各 41 个，
无 7-Zip 符号泄漏），Linux x86_64 静态库 `andzip_core.a` 30.9 MB 且被 `azcore` 成功链接，但
**未在这些平台上运行过测试**。`azgui.exe` 的压缩/解压/自检流程已用真实控件点击驱动并逐字节复核，
见[使用说明](#2-最小-gui-验证程序azgui)。

同一轮定向压力把「建包→加成员→finish→释放」在 CLR 宿主里跑了 5 组配置 × 6 次 × 1500
（zip/7z × 有密码/无密码 × 有回调/无回调 × 空包/单成员）：修复前每 1500 次左右崩一次，
修复后 45,000 次 0 崩溃。现在这条检查常驻在 `Andzip.Native.Tests` 里
（`writer stress`，4000 次加密 ZIP 写入 + 读回），并且做过反向验证——把 `PropEntry` 的默认
构造删掉重编内核后，6 次运行崩 5 次。定位思路见
[`../README.md` 第七节第 10 条](../README.md)。

`tests/native/test_main.cpp` 刻意不做任何依赖：几个宏、一个临时目录、公开头文件，所以库能
跑的地方它就能跑。覆盖范围：ABI/版本协商、格式与编码器注册表、15 组"格式×方法"的内存往返
并逐字节比对、多条目嵌套名称与流式读取、磁盘目录树、三种口令方案加口令回调、进度与取消、
路径穿越防护、已知 CRC32/SHA-256 值，以及各种错误路径（文件不存在、非归档、NULL 参数、
finish 之后再用、分卷请求被拒）。

自己造再自己读会掩盖对称性缺陷，所以有两个测试故意使用引擎没有生产出来的输入：
`TestForeignArchive` 按格式规范手工拼了一个 USTAR 镜像，测试集还会读由独立 `tar` 写出的归档。
正是这样发现了前面提到的格式探测 bug——tar、ext 以及所有 `SignatureOffset` 非零的格式都探测
不出来，而无论做多少"创建后打开"的测试都发现不了它。

---

## 已知限制

依赖某个功能前请先读这几条。

- **分卷输出未实现。** `az_create_options.volume_size` 给出可解析的大小时，
  `az_writer_open` 会以 `AZ_ERROR_UNSUPPORTED` 失败。这是故意的：由门面驱动 7-Zip 的
  `CMultiOutStream` 时会报告成功却**一个文件都不生成**，那是丢数据级别的故障，所以改成明确
  拒绝。**读取**分卷归档是好的——`IArchiveOpenVolumeCallback` 已实现，`archive.7z.001`
  会作为一个归档打开并从同目录取回其余分卷。
- **没有 `az_entry_find`。** 早先的版本导出过一个原生按名字查找的函数，它经过 P/Invoke 调用
  时会不确定地崩溃（`0xC0000005`）——包括在什么都没跑过的时候——而从 C++ 里做 60 轮交错调用
  又完全复现不了。因此查找改为在 `ArchiveReader.FindEntry` 里基于 `az_entry_get` 完成，这条
  路径两侧都被测试覆盖。被替换掉的那个引擎调用已从头文件、ABI 和导出表中删除，而不是留在那
  里当一个潜在崩溃点。等价的原语是 `ArchiveReader.Entries`；频繁查找应当先把它做成字典一次，
  而不是每次查询都扫一遍。
- **Linux 只验证到"能编译、能链接、导出正确"。** 见上面的对照表：没有可执行 Linux 二进制的
  环境，所以运行时行为仍是空白。POSIX 分支这次第一次真正被编译，修掉的问题是
  `FILETIME_To_timespec` 在全局作用域而非 `NWindows::NTime`、`PropVariantClear` 与
  `E_UNEXPECTED` 在 7-Zip 的 POSIX 头里不存在（`az_pch.h` 补了一处兼容声明）、
  `VT_LPSTR`/`pszVal` 不在模拟的 `PROPVARIANT` 里、`CFileInfo::Attrib` 在 POSIX 上要用
  `GetWinAttrib()`、以及 `FString` 在 POSIX 上是 UTF-8 `AString`（所以不该再套一层 `us2fs`）。
- **静态库的消费方必须整体链入。** 见 [静态库](#静态库已验证)：注册 handler 的目标文件没有被任何
  符号引用，普通静态链接会把它们丢掉，结果是"库能用、格式表是空的"。CMake 目标已处理，手工
  集成要加 `/WHOLEARCHIVE` 或 `--whole-archive`。
- **Windows arm64 只做了配置，没在这里构建过。** 配方在所有架构上都排除 7-Zip 的 `.asm`
  源文件、使用其 C 回退实现，这与 7-Zip 自己的 `arm64` makefile 一致，但没有产出 arm64 二进制。
- **格式自动探测需要字节签名或扩展名匹配。** 不带签名的 handler——Base64 和 Intel Hex——被
  刻意排除在内容探测之外：它们的 `IsArc` 几乎接受任何文本，否则一个普通 `.txt` 就会被当成
  含一个成员的"归档"打开。要打开这类文件请用 `az_open_options.forced_format`
  （或 `OpenOptions.ForcedFormat`）显式指名。这也意味着传给 `IsArc` 的缓冲区永远位于**归档
  起始处**，绝不是 `SignatureOffset` 处；7-Zip 的 handler 靠自己内部的偏移去够魔数，这就是
  tar（`ustar` 在 257）和 ext（超级块在 0x400）在被纠正之前完全探测不到的原因。
- **条目名称按构造一律是 `/` 分隔。** 7-Zip 的 handler 在 Windows 上读取时会把存储路径转换成
  操作系统分隔符（`Archive/Common/ItemNameUtils.cpp`），所以嵌套成员会以 `src\sub\n.txt`
  的形式到达。门面只在 Windows 上把 `\` 折回 `/`。在 POSIX 上反斜杠是普通的文件名字符、保持
  原样，因此 Windows 产生的、名称里含字面反斜杠的归档会被精确复现而不是被改写。
- **单流容器存不下多少元数据。** `xz` 不保存文件名和时间戳，`bzip2` 三者都不保存；要求它们
  保留名称，是在要求格式做它做不到的事。原生测试集精确记录了哪个容器保留哪些字段。
- **口令错误不一定能和"数据损坏"区分开。** 带加密头部的 7z 归档在打开阶段就失败，会被报成
  `AZ_ERROR_PASSWORD`。没有加密头部时不存在口令校验，唯一的信号是解密结果通不过验证；门面会把
  加密成员上的 data/CRC/unexpected-end 失败重新标注为 `AZ_ERROR_PASSWORD`，但那是推断，
  不是证明。
- **`az_writer` 只新建归档。** 不能往已有归档追加、不能删除成员、不能原地更新。引擎的
  `UpdateItems` 同样是做这件事的入口，但那意味着读出旧归档并重放它的全部成员，目前没实现。
- **`word_size` 的含义随方法而变。** 依据方法名它分别变成 PPMd 的 order（`"o"`）、LZMA 的
  `lc`、或 BZip2 的 pass 数，其余情况下被忽略并给出警告。需要更精确的控制请用
  `extra_properties`。
- **额外编码器是可选的，且上述通过的断言里没有开启。** `cmake/codecs.cmake` 是按 vendor
  之后的文件布局写的；请把 `-ExtraCodecs` 当作需要单独跑一次首次构建来看待。

---

## 重新 vendor 与再生成

`third_party/` 是生成的，`cmake/7zip-sources.cmake` 又是由它生成的。改动了 `cankao/` 里的
任何东西之后：

```powershell
pwsh native/scripts/vendor-sources.ps1     # 默认：原样 vendor 7-Zip，不叠加胶水层
node native/scripts/gen-sources.mjs        # 重新生成源文件清单（这一步需要 Node.js）
```

`cmake/7zip-sources.cmake` 是入库的，所以常规构建不需要 Node；只有重新生成清单时才用得到。

有两点是脚本刻意固化下来的，因为不明显：

- **`vendor-sources.ps1 -ExtraCodecs` 是独立的模式。** 7-Zip-zstd fork 的
  `ZstdDecoder.h` 与原生的 `Archive/ZstdHandler.cpp` 不兼容，所以开启额外编码器必须同时替换
  这个 handler。无条件叠加 fork 的文件会编译失败，这就是默认 vendor 不带胶水层的原因。
- **`gen-sources.mjs` 会补两个上游清单里没有的文件。** `Bundles/Format7zF/Arc.mak` 是
  `7z.dll` 的配方，它把文件 I/O 和分卷拆分推给控制台/GUI 前端。由于没有 vendor 任何 `UI/`
  代码，`FileStreams.cpp` 是手工加进来的。`MultiOutStream.cpp` 也曾加上、之后又移除——见上面
  的分卷限制。

生成器从 `7zip.mak` 里解析出 group→directory 映射，所以升级 7-Zip 版本后重跑一次，就会自动
带上新增和删除的 handler，并报告清单里已经不存在的源文件。

强制全量重来：`pwsh native/scripts/build-windows.ps1 -Clean`。

---

## 许可证与出处

`andzip_core` 合并了多个上游。`third_party/MANIFEST.json` 是"哪个目录来自哪个压缩包"的机器
可读记录；`cankao/` 保留原始 zip，未作改动。

| 组件 | 来源 | 许可证 |
|---|---|---|
| 7-Zip 引擎（`C/`、`CPP/`） | `cankao/7zip-main.zip` | LGPL-2.1-or-later **附 unRAR 限制**（`third_party/7zip/DOC/License.txt`、`unRarLicense.txt`） |
| zstd + zstdmt | `cankao/7-Zip-zstd-master.zip` | BSD-3-Clause 或 GPL-2.0（zstd）、MIT（zstdmt） |
| LZ4、BLAKE3、xxHash | `cankao/NanaZip-main.zip` | BSD-2-Clause / CC0-or-Apache-2.0 / BSD-2-Clause |
| Brotli、Lizard、LZ5 | `cankao/NanaZip-main.zip` | MIT / BSD-2-Clause |
| 上述组件的 7-Zip 封装 | `cankao/NanaZip-main.zip` | MIT（NanaZip） |
| Zig（仅用于交叉编译，不进产物） | ziglang.org | MIT |

`native/src`、`native/include`、`native/tools`、`native/tests` 下的全部
内容都是本项目的原创工作，沿用 Andzip 自身的许可证。

在发布链接了额外编码器的二进制之前，请对照你打算宣传的 RAR 支持范围复核 unRAR 条款，并确认
所使用的 LGPL 路线（计划是动态链接 `andzip_core`，见
[接入 Andzip.Core](#接入-andzipcore)）。
