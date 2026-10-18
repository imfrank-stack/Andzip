# P2-a：codec registry 撞名时谁赢

> 这是 `docs/压缩提速-执行流程.md` 第 6.1 节那一半的产出。
> 结论一部分来自 `native/third_party/7zip` 当前内容的行号，一部分来自已经跑过的
> `azcore version` / `azcore codecs`（它们直接把 `g_Codecs[]` 打出来，见第三节）。
> 唯一需要编译才能定死的那一步（真的注册两条同 ID 的条目，看编码方向落到谁身上）**已经在
> 2026-10-18 跑完了** —— 探针在 `native/tests/probe/`，读数在第三节末尾，原始输出在
> `docs/perf-p2a-probe.txt`。P0 基线那会儿没跑，是因为同时开编译会打歪它的计时。

---

## 一、读代码就能定死的四条事实

1. **注册表是一个 append-only 的数组。**
   `CPP/7zip/Common/CreateCoder.cpp:33-36`：
   `RegisterCodec()` 就是 `g_Codecs[g_NumCodecs++] = codecInfo`，没有去重、没有报错、没有覆盖。
   同 ID 注册两次 → 数组里两个条目，静悄悄。

2. **上限 64，超了静默丢弃。**
   同文件 `:14` `static const unsigned kNumCodecsMax = 64;` + `:34` 的 `if (g_NumCodecs < kNumCodecsMax)`。
   多加一个编码器之前必须先量现在有几个（见第三节），否则 `andzip_core` 会在"多一个 codec"之后
   少一个 —— 而且没有任何报错。

3. **查找按方向过滤，返回第一个匹配的条目。**
   `CreateCoder.cpp:206` 那个 static `FindMethod_Index(methodId, encode)`，循环体在 `:211-213`：
   ```cpp
   for (i = 0; i < g_NumCodecs; i++) {
     const CCodecInfo &codec = *g_Codecs[i];
     if (codec.Id == methodId && (encode ? codec.CreateEncoder : codec.CreateDecoder))
       return (int)i;
   }
   ```
   `encode` 为真时**只看 `CreateEncoder` 非空**的条目。这一句是整件事的钥匙。
   另外两个查找函数要说清楚，它们的行为不一样：`:160` 的 public `FindMethod_Index` 是**按名字**
   查（`StringsAreEqualNoCase_Ascii(name, codec.Name)`，同样带方向过滤）并且会把 `NumStreams` /
   `IsFilter` 从条目里读出来；`:236` 的 `FindMethod(methodId, name)` 只按 ID 匹配、**不看方向**，
   于是撞名时它报的是"排在前面的那一个"的名字 —— 所以新条目的 `Name` 要沿用 `"Deflate"`，
   别让"方法叫什么"这件小事也跟着链接顺序漂。

4. **ZIP 的 Deflate 编码器确实是从这张表取的。**
   `CPP/7zip/Archive/Zip/ZipAddCommon.cpp:382` 拼出 `methodId = kMethodId_ZipBase + method`
   （`ZipCompressionMode.h:17`：`kMethodId_ZipBase = 0x040100`，Deflate 的 method 号是 8 → `0x040108`，
   与 `Compress/DeflateRegister.cpp` 末尾 `REGISTER_CODEC_2(Deflate, CreateDec, CreateEnc, 0x40108, "Deflate")`
   对上）；`:385` 调 `CreateCoder_Id(methodId, true, _compressEncoder)`；`:388` 拿不到就 `return E_NOTIMPL`。

## 二、结论：不要跑去赢那个"第一个"，要让对方在 encode 这一侧不存在

上游 `DeflateRegister.cpp` 的编码器那一项是被一个**编译开关**控制的：

```cpp
#if !defined(Z7_EXTRACT_ONLY) && !defined(Z7_DEFLATE_EXTRACT_ONLY)
#include "DeflateEncoder.h"
...
REGISTER_CODEC_CREATE(CreateEnc, NEncoder::CCOMCoder)
#else
#define CreateEnc NULL
#endif
```

全树只有两个文件认这个开关（`DeflateRegister.cpp:8,17` 与 `Deflate64Register.cpp:8,17`），
上游自己就有一个 bundle 用它 —— `CPP/7zip/Bundles/Format7z/makefile:4` 带着 `-DZ7_DEFLATE_EXTRACT_ONLY`。
也就是说"把上游 Deflate 的**编码**方向从注册表里摘掉"是 7-Zip 自己支持的一种配法，
不是我们对上游源码动手术：**它只是一个 `-D`，写在 `native/cmake/` 里，vendor 一行不改。**

于是接一个新 Deflate 编码器有两条路：

| | 做法 | 稳不稳 |
|---|---|---|
| **A（推荐）** | 编译时 `-DZ7_DEFLATE_EXTRACT_ONLY`，上游条目变成 `CreateEncoder = NULL`；我们自己的 TU 用同一个 ID `0x040108` 注册一个"只有编码器、解码器给 NULL"的条目。`FindMethod_Index` 的方向过滤让两者各赢一边：压 → 只有我们匹配；解 → 只有上游匹配。**与顺序无关**。 | 稳，且失败模式是响的：万一我们的条目没注册上，`ZipAddCommon.cpp:388` 立刻 `E_NOTIMPL`，压 ZIP 当场报错，不会悄悄用回旧编码器 |
| **B（不要）** | 两边都注册完整条目，靠链接顺序让我们的 TU 排在前面，抢下"第一个匹配"。 | **脆**：`RegisterCodec` 的调用点是静态对象的构造函数（`RegisterCodec.h` 的 `REGISTER_CODEC` 宏展开成 `static REGISTER_CODEC_NAME(x) g_RegisterCodec_x;`），跨翻译单元的静态初始化顺序 C++ 不管、MSVC 只"按链接顺序实践上如此"。今天赢，明天换 CMake generator / 加一个源文件就可能输，而且**输了也不报错**——只是压出来的包换了个编码器，没人知道。 |

顺带一条被排除的路：**外部 codecs 机制顶不上**。两份 `FindMethod_Index` 都把 `_externalCodecs`
的搜索排在内部表之后（static 那份 `:217-227`、public 那份 `:187-196`，返回值都是 `g_NumCodecs + i`），
而且我们的构建根本没开 `Z7_EXTERNAL_CODECS`（`:22-30` 那段 `CHECK_GLOBAL_CODECS` 就在注释里）。

> 这张表里"A 推荐"是**读代码时**的判断。跑完之后它改成了推荐**就地替换**（见第三节最后）：
> 理由不是 A 不成立 —— A 成立，而且确实与顺序无关 —— 而是那个 `-D` 会连带把
> `Deflate64Register.cpp` 的编码器也摘掉，而 Deflate64 在 ZIP 那一排里现在写得出来。
> B 的判定不变：实测它输，且输得没有声音。

## 三、量到的，和跑出来的

### 量到的（`build\native\bin\azcore.exe version` 与 `codecs`，2026-10-18，当前 `-ExtraCodecs` 构建）

```
  formats     : 64
  codecs      : 30
```

`azcore codecs` 打的就是 `g_Codecs[]` 本身，所以它顺带回答了"注册顺序"这件事，
而且答案对 B 方案很不利：

| 位置（从 0 数） | 条目 | ENC | DEC |
|---|---|---|---|
| 14 | **Deflate**（上游的） | yes | yes |
| 13 | Deflate64（上游的） | yes | yes |
| 19..22 | Rar1 / Rar2 / Rar3 / Rar5 | **-** | yes |
| 25..29 | ZSTD / LZ4 / LZ5 / LIZARD / BROTLI（我们加的） | yes | yes |

三条结论：

- **离 64 还有 34 个位置**，加一条 Deflate 替身不会撞上 `kNumCodecsMax` 的静默丢弃 —— 这一项原本是
  "必须跑一次才知道"，现在量出来了。
- **我们加的 TU 一律排在上游之后**（`cmake/codecs.cmake` 的 `AZ_GLUE_SOURCES` 是
  `target_sources` 的最后一段，实测 25..29 就是这个顺序）。所以 B 方案不是"不稳"，
  是**照现在的接法直接就输**：上游 Deflate 在 14，我们的在 30 之后。
- **方向过滤是真的在用**：Rar1..5 那四条 ENC 是空的，它们照样待在表里、照样能被解码路径选中。
  "一个条目只提供一个方向"不是我们发明的用法，是上游自己的用法 —— A 方案踩在现成的机制上。

### 跑完了：探针的读数（`native/tests/probe/`，2026-10-18）

原来那四条"必须跑一次才算数"，用一个只属于实验的小程序验掉了。它注册一条 ID 同样是
`0x040108` 的条目，编码器指向自己写的 `CProbeCoder` —— 这个类只对一枚**私有 IID** 回答
`QueryInterface`，所以"这次创建出来的到底是谁"是问出来的，不是靠比对两个说不清名字的函数指针猜的。
整套东西放在 `native/tests/probe/`，`build-and-run.ps1` 一条命令跑完，**不进产品构建**：
它把 `andzip_core.lib` 当普通库链接（`/WHOLEARCHIVE`，和原生测试套件同一个理由：静态库里的
codec 注册靠静态对象的构造函数，不全拉进来就注册不上），自己带 `main`。原始输出整份存在
`docs/perf-p2a-probe.txt`。

**第一份：不开开关（＝ B 方案的形状）**

```
  before: g_NumCodecs = 30 (cap 64)
      index 15  name=Deflate        enc=yes dec=yes
  after:  g_NumCodecs = 31 (cap 64)
      index 15  name=Deflate        enc=yes dec=yes
      index 30  name=DeflateProbe   enc=yes dec=no
  encode encode=1 -> someone else's (NumStreams=1 isFilter=0)
  decode encode=0 -> someone else's (NumStreams=1 isFilter=0)
```

**注册式撞名直接输，而且是安静地输**：两边 `CreateEncoder` 都非空，方向过滤帮不上忙，
上游在 15、我们在 30，先匹配的那个赢。第二节给 B 方案写的"脆"，这是它落地一次的实测 ——
而且没有任何地方抱怨一句。

**第二份：开 `-DZ7_DEFLATE_EXTRACT_ONLY`（＝ A 方案的形状）**

```
  before: index 15  name=Deflate        enc=no dec=yes
  after : index 30  name=DeflateProbe   enc=yes dec=no
  encode encode=1 -> OUR probe coder (NumStreams=1 isFilter=0)
  decode encode=0 -> someone else's (NumStreams=1 isFilter=0)
```

**A 方案成立，而且与注册顺序无关** —— 我们的条目照样排在 30，照样赢 encode，
因为赢的原因是"上游那一格的 `CreateEncoder` 是 NULL"，不是"我们排在前面"。
反方向回到上游，解码路径一行没动。原来的第 1 条问"换到上游前后各测一次"，这里的答案是：
最不利的那一侧（排在后面）已经量过是赢的，排在前面只会更赢；这一条不需要两个方向都跑，
因为决定权不在顺序上。

顺带量到一处**上游的坑**，跟 P2-b 直接相关：开开关之后、注册我们那条之前，
`CreateCoder_Id(0x40108, encode=true)` 那一行打的是 `NumStreams=32759 isFilter=1` —— 这是栈上的垃圾。
成因是 `CreateCoder.cpp:453-455`：查不到条目时它 `index < 0 → return S_OK`，**一个字节都没写 `cod`**，
而 `CreateCoder.h:149-159` 那个 `CCreatedCoder` 的默认构造（`IsExternal(false), IsFilter(false), NumStreams(1)`）
是**注释掉的**（`:158`）。所以"P2-b 用 A 方案接进去之后，找不到编码器的那条路返回值一样是 S_OK"，
门面层必须自己看 `cod.Coder` 是不是空 —— 这正是第二节说的"A 的失败模式是响的"里唯一的漏洞：
响的那一声来自 `ZipAddCommon.cpp:388` 的 `E_NOTIMPL`（它判的是指针），不是来自 HRESULT。

**第三份：就地替换数组槽位（计划里没有的第三条路）**

不动编译开关、也不依赖链接顺序：注册之后把上游那一格的 `CreateEncoder` 换成自己的函数，
`CreateDecoder` 保持上游原样。两种构建里都成立：

```
  -- after replacing the vendor entry in place (keeping its decoder) --
  encode encode=1 -> OUR probe coder (NumStreams=1 isFilter=0)
  decode encode=0 -> someone else's (NumStreams=1 isFilter=0)
  decode  -> vendor's decoder (kept, as intended)
```

**这条比 A 更合这里的口味**，因为 A 有一处实在的代价，就是原来的第 2 条：同一个 `-D` 会连带
摘掉 `Deflate64Register.cpp:8,17` 的编码器。2026-10-18 把 ZIP 那一排方法逐个写了一遍
（`tests/perf/zip-methods.ps1`，读数在 `docs/perf-zip-methods.tsv`，256 KiB、level 3，两种输入）：
**可压的那一份**（基线 `text` 语料的前 256 KiB）里六个方法各写各的 —— Deflate 713 字节、
BZip2 343、LZMA 410、**Deflate64 1329**、PPMd 418、Copy 262294，`azcore list` 读回来的条目方法名
一一对得上（`Deflate` / `BZip2` / `LZMA:eos` / `Deflate64` / `PPMd` / `Store`）；只有 LZMA2 被内核拒
（`0x80004001` E_NOTIMPL，产物 0 字节）。**压不动的那一份**（同一长度的随机字节）六个方法全部
`exit=0`、产物一律"原样 + 包头"（这一份里是 150 字节，包头随存进包里的成员名长度变），
而读回来的条目方法**全是 `Store`**（LZMA 那条是 `Store:v1`）——
7-Zip 的 ZIP handler 在"压了不如不压"时留下小的那种表示法；`-x 0` 与 `-x 9` 各抽了一次，同样全是 `Store`。
所以看产物字节分不出方法与存储，
**必须把条目方法读回来**；这一列才是脚本存在的理由，也是 Deflate64 真的能写这件事的证据。
**也就是说 Deflate64 现在是能用的，开那个开关就是拿一个已经有的功能去换一个新的** —— 换不得。
就地替换不需要任何 `-D`，代价是它真的往 `g_Codecs[]` 里写了一格，属于"运行时改上游的数据结构"，
比加一个编译开关更难向后来的人解释；真要做，就把它放在一个能打断点的、只跑一次的初始化里，
别散在门面里。

**这个开关还咬过我们一口，咬的地方不在注册表上**：跑第二份探针要用 `-BuildRoot` 开一棵
实验构建树，而 `native/scripts/build-windows.ps1` 那一步"把引擎交给 .NET 一侧"的拷贝**不看
`-BuildRoot`** —— 它无条件把 `$BuildRoot\bin\runtimes\*` 覆盖进 `src\Andzip.Native\runtimes\`。
于是那份带 `-DZ7_DEFLATE_EXTRACT_ONLY` 的 `andzip_core.lib`（21 868 962 字节）把仓库里签入的
那份（21 878 374，当天 04:51 的干净构建）就地换掉了，而**没有任何一句提示**：仓库里从此躺着一份
"库存 Deflate 编码器被编译掉"的静态库，下一次打包会直接带着它走。已经修在形状上而不是这一次实验上：
那一步现在只认两棵产品构建树（`build\native`、`build\native-static`），别的根只把产物留在自己树里并说一句；
签入的那份已按 HEAD 还原。**教训记第七节**：实验可以改编译开关，但不能有一条不改源码就能写进仓库的路。

**原来的第 3 条（`NumStreams` / `IsFilter` 要对齐）**：上游 `DeflateRegister.cpp:23` 用的是
`REGISTER_CODEC_2`，宏体在 `Common/RegisterCodec.h:45-48`，展开成 `{ crDec, crEnc, id, name, 1, false }`
—— 所以"对齐"就是 `1, false`，探针照抄了这个取值，读数里两处 `NumStreams=1 isFilter=0` 是它。
为什么要对齐：门面 `native/src/api/az_formats.cpp:237` 的 `az_codec_get` 走的是
`Compress/CodecExports.cpp:199` 的 `GetMethodProperty`，而它就是
`const CCodecInfo &codec = *g_Codecs[codecIndex];` 直取表项，把这两项原样交出去 ——
`azcore codecs` 的 FILTER 那一列（Deflate 显示 `-`）就是 `IsFilter`，取错了会在 ABI 的对外面上留痕
（Core 的方法表、`az_codec_info.is_filter` 都跟着变）。
至于"`IsFilter` 当筛选条件"的 `FindCodecClassId`（`CodecExports.cpp:54,74`）：查了调用点，
`native/` 下（`third_party/` 除外）`FindCodecClassId` / `CreateCoder_ClassId` / `GetClassId` **零命中** ——
COM class-id 那条路我们根本没有入口。**这一条是查调用点查出来的，不是跑出来的**，标注清楚。

**读数之外的一件事**：探针（静态库）里上游 Deflate 在 **15**，而上面那张 `azcore codecs`（动态库）
的表里它是 **14** —— 同一份源码、两种构建，注册顺序差了一格。这不是笔误，是**"顺序"这件事本身不稳**的
第二个证据：连我们自己都复现不出两边一致。所以 P2-b 无论走 A 还是走就地替换，
判据都不能是"第几格"，只能是"这一格的 `CreateEncoder` 空不空"。

## 四、这件事对 P1 的反作用

`docs/perf-p1a-deflate-mt.md` 的结论是：上游 Deflate 编码器**没有 flush 接口**，块级并行要靠它做不到。
A 方案的含义正好补上这一块 —— 一旦"我们自己的 Deflate 编码器"是接得进去的，那么
"选一个**有** `Z_SYNC_FLUSH` 语义的实现"（zlib 的 `deflate()`、libdeflate 的 `deflate_zlib_compress` 之外的
raw 接口）就不再受上游约束。**所以接编码器（P2）是块级并行（P1）的前置**，不是可换顺序的两件事。
