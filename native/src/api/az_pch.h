// az_pch.h -- the 7-Zip headers every translation unit in src/api needs.
//
// Included by path relative to third_party/7zip (which is on the include path)
// rather than by 7-Zip's own "../../Common/..." style, so that "Common/X.h"
// can never be ambiguous between CPP/Common and CPP/7zip/Common.

#pragma once

#if defined(_MSC_VER)
#pragma warning(push)
#pragma warning(disable : 4100 4127 4244 4267 4702 4706 4996 28182)
#endif

#include "CPP/Common/MyWindows.h"
#include "CPP/Common/Common.h"
#include "CPP/Common/MyTypes.h"
#include "CPP/Common/MyUnknown.h"
#include "CPP/Common/MyCom.h"
#include "CPP/Common/MyString.h"
#include "CPP/Common/MyBuffer.h"
#include "CPP/Common/MyBuffer2.h"
#include "CPP/Common/MyVector.h"
#include "CPP/Common/ComTry.h"
#include "CPP/Common/Defs.h"
#include "CPP/Common/StringConvert.h"
#include "CPP/Common/StringToInt.h"
#include "CPP/Common/UTFConvert.h"
#include "CPP/Common/IntToString.h"
#include "CPP/Common/NewHandler.h"
#include "CPP/Common/Wildcard.h"

#include "CPP/Windows/PropVariant.h"
#include "CPP/Windows/PropVariantUtils.h"
#include "CPP/Windows/FileDir.h"
#include "CPP/Windows/FileFind.h"
#include "CPP/Windows/FileIO.h"
#include "CPP/Windows/FileName.h"
#include "CPP/Windows/TimeUtils.h"
#include "CPP/Windows/System.h"

#include "CPP/7zip/IStream.h"
#include "CPP/7zip/ICoder.h"
#include "CPP/7zip/IProgress.h"
#include "CPP/7zip/IPassword.h"
#include "CPP/7zip/PropID.h"
#include "CPP/7zip/Archive/IArchive.h"
#include "CPP/7zip/Common/FileStreams.h"
#include "CPP/7zip/Common/InBuffer.h"
#include "CPP/7zip/Common/LimitedStreams.h"
#include "CPP/7zip/Common/MethodProps.h"
#include "CPP/7zip/Common/ProgressUtils.h"
#include "CPP/7zip/Common/StreamObjects.h"
#include "CPP/7zip/Common/StreamUtils.h"

#include "C/7zTypes.h"
#include "C/Alloc.h"
#include "C/CpuArch.h"

#ifndef _WIN32
// 7-Zip's POSIX tree emulates the OLE PROPVARIANT helpers under its own names and
// omits some of the COM error codes. src/api is written against the Windows
// spelling on every platform, so supply the handful of missing pieces here rather
// than branching at each call site.
inline void PropVariantClear(PROPVARIANT *p) { NWindows::NCOM::PropVariant_Clear(p); }
// Only clears what was initialised: it dispatches on vt, so it must never be the
// first touch of an uninitialised PROPVARIANT. Use PropVariantInit for that.
inline void PropVariantInit(PROPVARIANT *p) { p->vt = VT_EMPTY; }
#define E_UNEXPECTED ((HRESULT)0x8000FFFFL)
#endif

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <filesystem>
#include <memory>
#include <string>
#include <vector>

#include "andzip/andzip.h"
