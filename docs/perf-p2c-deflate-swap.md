# P2-c：ZIP 与 7z 的 Deflate 编码器换成了 zlib-ng —— 三档同时变小，标准档快 9.2 倍，代价是一次数据丢失

> `docs/压缩提速-执行流程.md` 第 6.2 节停在"问人"那一句上，这一节是那句回答之后的落地：
> 「为它往 `native/third_party/` 再加一个第三方依赖」。
> 判据沿用 6.2 那两条（外部解压器逐字节一致；吞吐相对 P0 基线 ≥ 2× 且压缩率劣化不超过 2%），
> 没有另立，也没有放宽。读数全部落在文件里：`docs/perf-tiers.tsv`（阶梯 60 行）、
> `docs/perf-zlib-ng.tsv`（库本体 18 行）、`docs/decoder-compat.tsv`（外部解压器 15 行）。

## 0. 复现

```
pwsh native/scripts/vendor-sources.ps1 -ZlibNg <zlib-ng-checkout>
pwsh native/scripts/build-windows.ps1 -ExtraCodecs -DeflateZlibNg -RunTests
pwsh tests/perf/tiers.ps1                                        # 四排扫描 + 阶梯，整张 docs/perf-tiers.tsv
# 只想重跑阶梯那一排：加 -SkipAccept -SkipZip -Skip7z -SkipMem，并带上 -Append
#（不带 -Append 脚本先把表重置成表头，带了就是**追加** —— 表里已有那 60 行会再写一遍，
#  所以重跑阶梯之后要把旧行删掉再入库，这一版那 60 行就是这么来的）
dotnet run tests/perf/tier-check.cs                              # 8 格钉住产品路径的字节数
pwsh tests/decoder-compat.ps1                                    # 写 docs/decoder-compat.tsv
pwsh native/scripts/verify-all-targets.ps1 -SkipWindows -DeflateZlibNg
```

- 版本钉在 commit：zlib-ng `develop @ 1727258`（`zlibVersion()` 报 `1.3.2.zlib-ng`）。
  上游**没有 v2 正式版标签**（只有 `v2.0.0-RC1/RC2`），所以 `-ZlibNgSha` 记的是 40 位 commit，
  不记 tag —— `native/third_party/MANIFEST.json` 那一行同样只写 commit。
- `ci.ps1` 的两条引擎作业都显式带 `-DeflateZlibNg`：不带它产出来的是**没量过的那一份**。

## 1. 换的是一个注册槽位，不是一种新算法

`0x40108` 这一个 method id 同时是 ZIP 条目的和 7z 成员的：`7zHeader.h:113` 定义
`k_Deflate = 0x40108`，ZIP 那边由 `ZipAddCommon.cpp:382` 的 `kMethodId_ZipBase(0x040100) + 8`
算出同一个数。所以"换掉 ZIP 的 Deflate"和"换掉 7z 的 Deflate"在引擎里不是两件事。

换法用的是 P2-a 量出来的那条（`docs/perf-p2a-registry.md`）：**不新增注册项，把 vendor 那一条的
`CreateEncoder` 指针原地换掉**（`native/src/codecs/az_deflate_swap.cpp:42-60`），身份判据是
`entry->Id == 0x40108 && entry->CreateDecoder != NULL`，不是数组下标 —— 下标在不同构建里是 14 还是 15，
P2-a 撞过这个雷。三条边界：

- **解码方向一行不碰**：`CreateDecoder` 原样留着，那仍然是 vendor 的 NDeflate。
- **Deflate64 不换**：它自己那条（`0x40109`）不在替换范围内。这正是"原地改一个指针"而不是
  "编译期把整个 Deflate 模块关掉"的原因 —— 后者会把 Deflate64 的编码器一起带走。
- **换不上会说话**：`az_writer.cpp:665` 拿 `AzDeflateSwapInstall()` 的返回值走既有的 `problems`
  通道，报"这一包是引擎自己的 Deflate 压的"，不静默降级。

## 2. 档位映射：`MapTierLevelToZlib`，理由只能由阶梯给

7-Zip 的 `-mx=` 是 0..9，zlib 的 level 也是 0..9，但**不是同一条刻度**：库本体在
`docs/perf-zlib-ng.tsv` 里 1..9 每一格都量过（128 MiB `mixed`、raw deflate、流式那一半）。
先把产品路径的 0..9 全跑一遍（`tests/perf/tiers.ps1` 的阶梯），再从这个表里挑：

| 界面那一档 | 引擎 x | 下发给 zlib | 为什么是这一格（库本体的读数） |
|---|---|---|---|
| 仅存储 | 0 | 0 | deflate 的 stored block，产物 = 原尺寸 |
| 极速 / 快速 | 1 / 3 | 2 | 1 档要 0.18 s 但产物 42 699 963；2 档 0.30 s / 40 420 857 —— **多花 0.12 s 换小 5.3%**，而 2 档放到产品路径上仍然比改前的极速快 2.19 倍（0.79 s → 0.36 s） |
| 标准 | 5 | 6 | 5 与 4 档在库本体里逐字节相同（40 636 135），6 档才跳到 39 782 926 —— 小 2.1% 只多 0.04 s |
| 极限 | 9 | 8 | **9 档比 8 档大**：39 776 993 对 39 768 104（大 8 889 字节），而极限档的定义是"只管压得小" |
| （其余） | 4 / 7 / 8 | 原值 | 阶梯里这三格就是按原值跑的，没有特判 |

映射生效的证据是阶梯里那三对**逐字节相同**的行：`defladder mixed zip Deflate` 的 1/2/3 三格都是
40 421 017，5/6 都是 39 783 086，8/9 都是 39 768 264。除此之外没有别的办法从外部看出映射有没有落地。

顺带一条交叉核对，产品与库本体对得上：那三格的产物字节与 `docs/perf-zlib-ng.tsv` 同 level 的读数
**差一个常数 160**（40 421 017 / 40 420 857、39 783 086 / 39 782 926、39 768 264 / 39 768 104），
7z 那一侧是同一个 offset 换一种容器：**差 130**。三个不同尺寸上偏量一模一样 —— 也就是说 coder 交出去的
那一条流在两处是同一份字节，差的全是容器自己那点头部。（这里量的是**整包**尺寸，不是成员尺寸，
所以 160 这个数不拆成"局部头 + 描述符"——拆不出，也没必要拆。）

## 3. `a=0` 从 Deflate 上摘掉：不是收敛，是性质变了

5.6.0 发出去的 `a=0` 是给 vendor 编码器换一档 huffman 构建（`docs/perf-tiers.tsv` 里那一排
`props=a=0` 的读数就是它）。新 coder 的属性面只有两条：认 `x`（`kLevel`），忽略 `mt`
（一条 deflate 流就是一个线程），其余一律 `E_INVALIDARG`（`native/src/codecs/az_deflate_encoder.cpp:158-169`）。
于是同一个 `a` 从"被换了一种用法"变成"**让整次压缩失败**"。这不是保守，是把它留在表上的代价：
一条永远点不动的按钮。所以 `NativeEngine.cs` 的 `TierExtras` 现在只给 Deflate64 发 `a=0`。

两条断言把这件事钉住（`native/tests/native/test_main.cpp` 的 `TestExtraProperties`）：
用 **Deflate64** 证明 `extra_properties` 这条通道还在（fb / mc / pass 照旧能落到 coder 上），
用 **Deflate** 证明拒绝是响亮的、而且**不留下半个包**。后半那条顺带修了一个真的坑：属性是在
输出文件建好之后才下发的，所以 coder 侧拒绝时磁盘上已经有一个空文件了 —— 现在 `az_writer.cpp:572-578`
记 `Committed` / `CreatedFile` 两个标志，析构里（`:623-627`）只删"没提交、且长度为 0"的那一个，
`Committed` 在 `az_writer_finish` 的两个成功出口**之前**置位（`:1523-1529`）。注意不能用 `Finished`：
它在 `az_writer_finish` 的**开头**就置位，中途失败的那一次同样是 true。

## 4. 读数：三档全部同时变小、变快

`docs/perf-tiers.tsv`，128 MiB 单条目、每格 2 次取小的那一遍。"改前"那一列是 5.6.0 实际发出去的
行为（极限档带 `a=0`）：

| 档位 | 改前（字节 / 秒） | 改后（字节 / 秒） | 变化 |
|---|---|---|---|
| 极速 1 | 40 972 439 / 0.79 | 40 421 017 / 0.36 | 小 1.35%、**快 2.19 倍** |
| 标准 5 | 40 710 824 / 5.05 | 39 783 086 / 0.55 | 小 2.28%、**快 9.18 倍** |
| 极限 9 | 39 983 091 / 6.96 | 39 768 264 / 1.06 | 小 0.54%、**快 6.57 倍** |

再给一条不带 `a=0` 的对照，因为那才是"vendor 的 9 档本来长什么样"：
**39 656 224 字节 / 91.42 秒**。新编码器比它**大 0.28%**（112 040 字节），同时**快 86 倍** ——
这一格是这一轮唯一"不是全都更小"的对照，写清楚：5.6.0 那次 `a=0` 买到的 13.1 倍，在它前面不算什么，
但"极限档压得最小"这条承诺的对标对象是**上一版发出去的行为**（39 983 091），不是这一格。

- 门槛：6.2 要 ≥ 2×，实测最差的一格（极速）是 2.19×；压缩率那一半没有"劣化不超过 2%"这回事，
  **三格都变小了**。
- 对 P0 基线：`docs/perf-baseline.md` 的 headline 那格 `zip/Deflate/5 single` 是 22.8 MB/s，
  这一轮同一条路径是 **232.7 MB/s**。
- 7z 那一排同样：39 783 056（标准）/ 39 768 234（极限）。
- `rand` 语料：134 217 886，比例 1.0000 —— 不可压数据上 deflate 发 stored block，
  快慢只差在"有没有做无用功"，产物尺寸与压缩前一样。这一格改前改后都是这个形状。

**为什么没有重跑 `tests/perf/bench.ps1`**：6.2 那句"重跑 bench.ps1"写的时候没料到它的
`-Tsv` 默认就是 `docs/perf-results.tsv` —— **那份文件就是 P0 基线本身**。重跑会把我正在引用的
那几行覆盖掉，把比较的两边变成同一次测量。阶梯是同一份语料、同一个入口、同一台机器的更细版本，
所以按阶梯引用，基线表保持原样。

## 5. 那 16 472 字节：一次只有"外面的人读得回来"才能看见的数据丢失

换完第一遍跑出来的 ZIP 成员，比 coder 实际吐出的字节**少 16 472**，解出来 3 470 298 / 8 388 608 并报
data error。而当时**整套原生断言全绿（那条回归就是它教出来的，补上之后才数到 718）、阶梯的产物字节也都对**——阶梯看的是包尺寸，
"少写"恰好表现为包更小、压得更快。**这条 bug 会把自己伪装成一次提速。**

成因是 I/O 契约，不是 deflate：`ISequentialOutStream::Write` 允许少消费，实际消费量必须从
`*processedSize` 读回来，而我传了 `NULL`。vendor 的 `COutBuffer::FlushPart`（`OutBuffer.cpp:69-72`）
守的就是这条规矩，它自己的 coder 也是按 1 MiB 的输出缓冲在排空。修法同处
（`az_deflate_encoder.cpp:246-256`）：`while (left)` 循环写，`processedOut == 0` 或超过剩余量就
`E_FAIL`，绝不静默少写。诊断用的临时构建（`AZ_DEFLATE_DIAG`）已逐行删除。

回归补了一条 `TestLargeMemberFromFileRoundTrip`：8 MiB 走真实文件，zip + 7z 两种容器，
压完解压回来逐字节比。教训记一条，因为它直接改变了这一阶段的验收：**"包更小、压得更快"
不能单独当验收**——所以第 7 节那三家外部解压器是这一阶段的门槛，不是收尾的装饰。

## 6. 阶梯里没被抹平的一条：`text` 语料上"越靠后越小"不成立

`text` 那一排：标准 201 782 **<** 极速 212 918 ≈ 极限 212 908 —— 极限档比标准档**大 5.5%**。
改前也反，只是赢家从极速挪到了标准（259 346 < 263 318）。

不"修"它是量出来的决定，不是懒得管：zlib 在不同 level 上换的是**匹配策略**
（4 档 filtered、5/6 档 medium、7 档以上 slow），在高冗余语料上 slow 反而给不出更小的包。
用一条 `if (corpus == text)` 把它特判掉，才是给下一次埋雷。界面上那句档位说明讲的是速度次序，
本来就没承诺"一定更小"；这一格的反常写在这里，`docs/perf-tiers.tsv` 里有对应的 20 行原始数据。

## 7. 外部解压器：三家，逐字节一致

`tests/decoder-compat.ps1` → `docs/decoder-compat.tsv`（5 个包 × 3 家 = 15 行）：

| 包 | 字节 | 7-Zip | Python `zipfile` | 资源管理器 |
|---|---|---|---|---|
| zip 极速 / 标准 / 极限 | 2 527 511 / 2 486 854 / 2 485 874 | identical | identical（含 CRC 校验） | identical |
| 7z 标准 / 极限 | 2 486 824 / 2 485 844 | identical | 没有 7z 后端 | 没有 7z 后端 |

源文件 SHA-256 `2E69F169657F80DA…D0D419`，15 行里 11 行是 identical bytes，其余 4 行是明写的
"这个解压器读不了 7z"，0 行 FAIL。

6.2 那一节写的是"四家都要真的试"，**这一轮只有三家**：WinRAR 本机没装，Bandizip 没有可无人值守的
命令行（脚本里曾经留过它的探测分支，已经删干净 —— 不留一条跑不到的路）。剩下那两家留给手动核对。
另一条要说清的独立性：7-Zip 用的解码器就是引擎现在仍在用的那一份 NDeflate，它独立于 zlib-ng、
但**不独立于 vendor 树**；真正跨实现的只有 Python（CPython 自带一份 zlib）和资源管理器
（`System.IO.Compression`）。资源管理器那条是异步的 `CopyHere`，所以脚本等文件出现、再等尺寸
连续两轮不动，不看退出码（它没有）。

Deflate64 故意不在这张表里：这一阶段没动它，而且这几家里有一半读不了 Deflate64 的 ZIP 成员 ——
那会把"不支持"写在本该做真核对的格子里。

## 8. 交叉编译这一轮挖出来两件事，都不是 zlib-ng 引起的，但都被它照亮

1. **`az_writer.cpp:1370` 的 `::getpid()` 在 Linux 上从来没编过。** 打开 `-DeflateZlibNg` 之后
   第一次配置就死在这里。用同一条命令行去掉 zlib-ng 的 include 与宏再编一遍，**一模一样地失败** ——
   与本轮无关，是 `76a2c15`（包内增删那一次）带进来的，之后没人再跑过 `-SkipWindows`，
   而 `native/README.md` 那一行的 ✅ 是从那之前带上来的。修法放在共享层：
   `native/src/api/az_pch.h:59-65` 那个 POSIX 块里补 `#include <unistd.h>` —— 那个块本来就写着
   它存在的理由："src/api 在所有平台上都按 Windows 的写法写，所以缺的那几件在这里补齐，
   而不是在每个调用点分支"。
2. **符号泄漏：x86_64 的 `.so` 导出 67 个，而不是 43 个**，多出来的 24 个全是 zlib-ng 的
   （`deflate`、`inflate*`、`crc32*`、`zlibVersion`），`native/scripts/check-exports.ps1` 当场把门禁拦死。
   这不是整齐问题：ELF 共享对象的导出符号进的是**整个进程的查找域**，我们这一份 zlib 会去应答同
   进程里别人家的 `deflate` 调用 —— 而 `ZLIB_COMPAT=ON` 恰恰保证了名字与 ABI 看着对得上，
   出错时会非常难查。常规答案 `-Wl,--exclude-libs,ALL` 在 zig 的驱动上直接
   `error: unsupported linker arg`，所以改成编译期做：`native/cmake/codecs.cmake:260-266` 给
   zlib-ng 的**真**目标设 `C_VISIBILITY_PRESET hidden`（`zlib-ng-static` 在 `ZLIB_COMPAT` 下是个
   alias，属性挂在 alias 上会被 CMake 拒绝 —— 先用 `ALIASED_TARGET` 解一层）。
   三个 Linux 目标现在都是 43/43。

两条顺带的实测更新：`libandzip_core.a` 从 30.9 MB 变 **31.3 MB**（README 表里那个数已按实测改）；
MSVC 忽略这个可见性属性，所以 Windows 的字节没动 —— 改完重跑 `tier-check.cs`，8 格逐格相同。

## 9. 没测的部分（这一条清单是给下一次省钱的）

- **解压方向**：一行代码没改（第 1 节），但也没重新量它的速度。
- **多条目包**：阶梯、外部解压器、回归用例都是单条目。多条目走的是同一个 coder，
  但"同一个 coder × N 条"没有读数。
- **solid / 分卷 / 加密与 deflate 的组合**：原生用例覆盖分卷与加密，但不是和新编码器一起量的。
- **Deflate64**：明确不换，所以它的读数还是 5.6.0 那一份。
- **musl 三元组、真 Linux 运行时**：这台机器没有可执行环境，
  `native/README.md` 那句"Linux 只验证到能编译、能链接、导出正确"照旧成立。
- **P1′ 那句"改用 7z"的 6.9–9.2 倍没重测**。它比的是同一种 coder 在"一个条目"与"十六个条目"两种
  形状下的吞吐，两边都换成了新编码器，所以倍数*理应*还在 —— 但没重量就没有证人。
  另一件连带的事更该记住：`docs/perf-baseline.md` 里 Deflate 那几行的**绝对吞吐现在是改前的**
  （`zip/Deflate/5 single` 那行 22.8 MB/s，产品路径这一轮是 232.7 MB/s）。那张表是 P0 的记录，
  不该被覆盖，但它也不该被当成今天的成绩单读。
- **WinRAR / Bandizip**：见第 7 节，手动补。

## 10. 门禁与产物读数

- 原生：**718 条断言 0 失败**，动态库与静态库两条作业各跑一遍。
- 导出表：Windows PE 与三个 Linux ELF 都是 **43/43**。
- 交叉编译：x86_64 动态 / aarch64 动态 / x86_64 静态三个目标都在 `-DeflateZlibNg=ON` 下构建通过。
- 产品路径钉格：`tests/perf/tier-check.cs` 8 格与 `docs/perf-tiers.tsv` 逐格相同。
- 外部解压器：15 行 0 FAIL（第 7 节）。
- `pwsh build.ps1` 出包 + 对产物跑 `/selftest`；界面由使用者手动核对（ZIP 与 7z × 五档，
  其中 128 MiB 的极限档现在是**一秒级**，这一条本身就是界面能感觉到的变化）。
