# P1-a：块级并行 Deflate 的可行性判读（只读代码，未改一行）

> 依据 `native/third_party/7zip/CPP/7zip/Compress/` 当前内容，行号随 vendor 升级会漂移。
> 流程文档第 5.1 节要求的四个问题，逐条给依据。

## 一、`CCoder` 是 one-shot，不是 streaming

- 对外只有 `CodeReal` / `BaseCode`（`DeflateEncoder.h:169-173`），COM 侧只有
  `ICompressCoder + ICompressSetCoderProperties`（`DeflateEncoder.h:179-188`）；
  `CCOMCoder::Code` 就是 `BaseCode`（`DeflateEncoder.cpp:1031-1033`）。
- `Code()` 的入参 `inSize` / `outSize` **被忽略**（`DeflateEncoder.cpp:969-970` 形参注释掉了），
  它靠"喂到输入流没字节为止"结束：

```cpp
// DeflateEncoder.cpp:1000-1013
do {
  t.BlockSizeRes = kBlockUncompressedSizeThreshold;
  GetBlockPrice(1, m_NumDivPasses);
  CodeBlock(1, Inline_MatchFinder_GetNumAvailableBytes(&_lzInWindow) == 0);   // ← 最后一步才置 final
  ...
} while (Inline_MatchFinder_GetNumAvailableBytes(&_lzInWindow) != 0);
```

**结论**：一次 `Code()` = 一条**完整**的 deflate 流，末尾必然 BFINAL=1。没有"喂一半、停下来、
再喂"的入口，`Code()` 也不能带着历史继续（见第三条）。

## 二、没有 Z_SYNC_FLUSH / Z_FULL_FLUSH 的等价能力

- 唯一像 flush 的是 `m_OutStream.Flush()`（`DeflateEncoder.cpp:1020`），而它是
  `BitlEncoder::Flush()` = `FlushByte()`（补最后一个不完整字节）+ 输出缓冲刷盘
  （`BitlEncoder.h:24-28`），只在流末尾调一次。SYNC_FLUSH 要的是"结束当前块 + 补一个空的
  stored 块 + 字节对齐、但流继续"，这个组合**没有对外入口**。
- 零件其实都在，但全是私有的：`WriteStoreBlock(blockSize, additionalOffset, finalBlock)`
  （`DeflateEncoder.cpp:784-792`，写 LEN/NLEN 并 `FlushByte()`）、`WriteBits`、`WriteBlockData`。
  要用它们就得把 `CCoder` 的私有段暴露出来 —— 那是改 `third_party/`，红线。

## 三、一个实例压完一段之后能不能复位再压下一段

- 能"复用实例"，但不是 `deflateReset` 语义：`CodeReal` 开头把状态全量重初始化
  （`Create()` at `:983`、`MatchFinder_Init` at `:989`、`m_OutStream.SetStream/Init` at `:990-991`、
  `m_OptimumEndIndex = m_OptimumCurrentIndex = 0` at `:993`、`t.InitStructures()` /
  `m_AdditionalOffset = 0` at `:995-999`）。
- 所以对同一个实例调两次 `Code()` 得到的是**两条互相独立的流**，各自 BFINAL=1，
  第二条不继承第一条的 32 KB 窗口。编码器侧也**没有预设字典**接口（`ICompressSetDicProps`
  一类在 deflate 编码器上不存在），而这正是 pigz 那类"分块带上下文"的做法所需的东西。

## 四、输出是 raw deflate，结尾置 BFINAL=1

- `CodeReal` 不写任何容器头（zlib 的两字节头 + adler32 是**另一个**编码器
  `ZlibEncoder.cpp` 干的事），产出即 raw deflate（等价 `wbits=-15`），ZIP 直接塞进条目。
- final 位的来源只有一处：`CodeBlock(1, 输入耗尽)`（`:1005`）→ `WriteBits(kFinalBlock …)`
  （`:933`）；空的 stored 块才带 final（`:790` 的 `finalBlock && blockSize == 0`）。
- 每条流结束时字节对齐（第一条末尾 `Flush()` 会 `FlushByte()`）——这一点对"直接拼接"是必要的，
  但**不充分**。

## 两条拼接方案的判断

**方案 A（一块一实例，块间 SYNC_FLUSH，字节对齐后直接拼）：不可行。**
它要求编码器"结束当前块但不要把流收尾"，而第一节和第二节合起来说明这个能力在本树里根本不存在，
除非暴露 `CCoder` 的私有成员（改 vendor，红线）。

**方案 B（各块都是完整独立流，再把除末块外的 BFINAL 位清 0）：代价与收益不匹配。**
BFINAL 是**每个块头**的第一位，而块是位对齐的变长结构：要找到某个流的"最后一个块的起点"，
必须从流头走一遍 —— 动态哈夫曼块得先解出码表才能读到 EOB 符号（`BTYPE=11` 还是非法块）。
这等于为了拼字节，自己写一个 inflate 前端解析器；它每一步都错不起，而错的后果正是流程文档
最反对的那一类：**产出一个"压得快但解不开"的包**。仅凭"每条流字节对齐"这一点不足以绕开。

## 结论（给 P1 的判词）

不建议在 7-Zip 自带的 Deflate 编码器上做块级并行。真正的拦路石不是"ZIP 按条目并行"，而是这个
编码器**没有 flush 接口**：块级并行 deflate 的标准做法（zlib 系：每块 `Z_NO_FLUSH`，块间
`Z_SYNC_FLUSH`，只在最后一块 `Z_FINISH`）在这里没有对应 API；绕开它就要写 inflate 块解析器去改
BFINAL 位。所以 **P1 不是独立阶段，它是 P2 的前置** —— 先接一个"可流式、可控 final 位"的
deflate 编码器（选型标准是**有没有 SYNC_FLUSH**，不是谁跑得快）。

接法在 `docs/perf-p2a-registry.md` 里已经判读清楚了，一句话：不要跑去赢"谁先注册"
（实测我们加的 codec 一律排在上游之后，`azcore codecs` 里上游 Deflate 在第 14 位、
ZSTD 那五个在 25..29），要用上游自己支持的编译开关 `Z7_DEFLATE_EXTRACT_ONLY`
把上游条目的**编码方向**摘掉，让我们的条目成为 `encode=true` 唯一的匹配 —— 与顺序无关。
在此之前，把零风险的 P1′（大单条目提示换 7z）做掉。

**我不确定的地方**：`Z7_DEFLATE_EXTRACT_ONLY` 这一手在同 ID 下的实际选取行为还没有跑过实验
（要编译，见那份文档第三节列的四条）；以及 7-Zip 后续版本会不会给 deflate 编码器加 flush
（本树 26.04 没有）。
