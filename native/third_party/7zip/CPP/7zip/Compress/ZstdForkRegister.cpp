// (C) 2016 Tino Reichardt

#include "StdAfx.h"

#include "../Common/RegisterCodec.h"

#include "ZstdForkDecoder.h"

#ifndef Z7_EXTRACT_ONLY
#include "ZstdForkEncoder.h"
#endif

REGISTER_CODEC_E(
  ZSTD,
  NCompress::NZSTD::CDecoder(),
  NCompress::NZSTD::CEncoder(),
  0x4F71101, "ZSTD")
