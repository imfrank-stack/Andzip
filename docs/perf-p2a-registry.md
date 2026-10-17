# P2-a：codec registry 撞名时谁赢

> 这是 `docs/压缩提速-执行流程.md` 第 6.1 节那一半的产出。
> 结论一部分来自 `native/third_party/7zip` 当前内容的行号，一部分来自已经跑过的
> `azcore version` / `azcore codecs`（它们直接把 `g_Codecs[]` 打出来，见第三节）。
> **还没有**跑的实验只有一个：真的注册两个同 ID 的 codec 看谁被选中 —— 那一步要编译，
> 而此刻 P0 基线正在测，编译会打歪它的数字。所以本文分成
> 「读代码 + 现成工具就能定死的」和「必须跑一次才算数的」两段，后者逐条列清楚。

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

## 三、已经量到的 / 还要跑一次才算数的

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

### 还要跑一次才算数的（等 P0 基线跑完再开编译）

1. 开 `-DZ7_DEFLATE_EXTRACT_ONLY` + 用同一个 ID `0x040108` 注册一个只有编码器的条目之后，
   `FindMethod_Index(0x040108, encode=true)` 与 `(encode=false)` 各自回到哪一条（按 `Name`
   与函数指针判断），并且**把这个条目的注册顺序换到上游前后各测一次**，确认结论真的与顺序无关 ——
   这一条才是 A 与 B 的分水岭。
2. `Z7_DEFLATE_EXTRACT_ONLY` 的连带影响：`Deflate64Register.cpp` 也会被它摘掉编码器。
   要确认我们的 ZIP/Deflate64 写入路径本来就写不了（`CompressionMethodOption` 那侧不给选），
   否则是拿一个新功能换掉一个旧功能。
3. 我们的条目要与上游条目在 `NumStreams` / `IsFilter` 上取值一致（上游是 `1, false`）。
   这两个字段不是摆设：门面 `native/src/api/az_formats.cpp:237` 的 `az_codec_get` 走的是
   `Compress/CodecExports.cpp:199` 的 `GetMethodProperty`，而它就是
   `const CCodecInfo &codec = *g_Codecs[codecIndex];` 直取表项，把 `NumStreams` / `IsFilter`
   原样交出去 —— 也就是说 **`azcore codecs` 打出来的那一列就是这两项**，取错了会在 ABI 的
   对外面上留痕（Core 的方法表、`az_codec_info.is_filter` 都会跟着变）。
   同一个文件 `:54,74` 的 `FindCodecClassId` 还拿 `IsFilter` 当筛选条件
   （`(isFilter ? !codec.IsFilter : codec.IsFilter)`），那是 COM class-id 那条路；
   压缩路径用的是 `CreateCoder_Id`（按 ID），不经过它 —— 这一条要在实验里确认"确实不相干"，
   而不是当成前提。

## 四、这件事对 P1 的反作用

`docs/perf-p1a-deflate-mt.md` 的结论是：上游 Deflate 编码器**没有 flush 接口**，块级并行要靠它做不到。
A 方案的含义正好补上这一块 —— 一旦"我们自己的 Deflate 编码器"是接得进去的，那么
"选一个**有** `Z_SYNC_FLUSH` 语义的实现"（zlib 的 `deflate()`、libdeflate 的 `deflate_zlib_compress` 之外的
raw 接口）就不再受上游约束。**所以接编码器（P2）是块级并行（P1）的前置**，不是可换顺序的两件事。
