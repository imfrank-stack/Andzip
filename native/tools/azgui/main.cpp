// main.cpp -- azgui: the smallest thing that proves andzip_core compresses and
// extracts, driven by hand rather than by a test harness.
//
// Plain Win32 (no framework, no third-party UI library) so the binary depends on
// nothing but andzip_core.dll, comctl32 and the CRT. Long operations run on a
// worker thread; the engine's progress callback can fire on a coder thread, so
// it never touches a control directly -- it posts a message and the window
// procedure does the updating. All Chinese UI text lives in this file; ops.cpp
// stays ASCII so the two concerns stay separate.

#include "ops.h"

#include "andzip/andzip.h"

#include <windows.h>
#include <windowsx.h>   // Button_GetCheck / Button_SetCheck
#include <commctrl.h>
#include <shobjidl.h>

#include <atomic>
#include <cstdio>
#include <ctime>
#include <filesystem>
#include <functional>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")

namespace fs = std::filesystem;

namespace {

enum Id
{
  ID_EDIT_SRC = 100,
  ID_BTN_SRC_FILE,
  ID_BTN_SRC_DIR,
  ID_COMBO_FMT,
  ID_COMBO_METHOD,
  ID_COMBO_LEVEL,
  ID_EDIT_PASSC,
  ID_CHK_RECURSE,
  ID_CHK_ENCHDR,
  ID_CHK_SOLID,
  ID_BTN_COMPRESS,
  ID_CHK_ROUNDTRIP,

  ID_EDIT_ARC,
  ID_BTN_ARC,
  ID_EDIT_DEST,
  ID_BTN_DEST,
  ID_EDIT_PASSX,
  ID_CHK_TESTONLY,
  ID_BTN_LIST,
  ID_BTN_EXTRACT,

  ID_BTN_SELFTEST,
  ID_BTN_FORMATS,
  ID_BTN_CODECS,

  ID_LIST,
  ID_PROGRESS,
  ID_STATUS,
};

#define WM_APP_PROGRESS (WM_APP + 1)   // lParam = heap Progress
#define WM_APP_RESULT   (WM_APP + 2)   // lParam = heap Result

// What the UI thread does with a finished job.
enum ResultKind
{
  RESULT_STATUS = 0,     // just write the status line
  RESULT_LINES  = 1,     // fill the list view with text lines, and the status
  RESULT_ENTRIES = 2,    // fill the list view with archive entries
};

struct Result
{
  bool ok;
  std::wstring text;
  int kind;
  std::vector<azgui::Entry> entries;
};

struct UI
{
  HWND Main = nullptr;
  HWND EditSrc = nullptr;
  HWND ComboFmt = nullptr;
  HWND ComboMethod = nullptr;
  HWND ComboLevel = nullptr;
  HWND EditPassC = nullptr;
  HWND ChkRecurse = nullptr;
  HWND ChkSolid = nullptr;
  HWND ChkEncHdr = nullptr;
  HWND ChkRoundTrip = nullptr;
  HWND EditArc = nullptr;
  HWND EditDest = nullptr;
  HWND EditPassX = nullptr;
  HWND ChkTestOnly = nullptr;
  HWND List = nullptr;
  HWND Progress = nullptr;
  HWND Status = nullptr;

  std::atomic<bool> Busy{ false };
  std::atomic<unsigned long long> LastPosted{ 0 };
};

UI g;

// ------------------------------------------------------------------- text

std::wstring GetText(HWND h)
{
  const int n = (int)::GetWindowTextLengthW(h);
  std::wstring s((size_t)n, L'\0');
  if (n)
    ::GetWindowTextW(h, s.data(), n + 1);
  return s;
}

void SetText(HWND h, const std::wstring &s)
{
  ::SetWindowTextW(h, s.c_str());
}

std::string GetUtf8(HWND h) { return azgui::ToUtf8(GetText(h)); }

bool Checked(HWND h) { return Button_GetCheck(h) == BST_CHECKED; }

std::wstring WithCommas(unsigned long long v)
{
  std::wstring s = std::to_wstring(v);
  for (int at = (int)s.size() - 3; at > 0; at -= 3)
    s.insert((size_t)at, L",");
  return s;
}

std::wstring HexU32(unsigned v)
{
  wchar_t b[12];
  swprintf(b, 12, L"%08X", v);
  return b;
}

std::wstring FormatMtime(long long unixSeconds)
{
  if (unixSeconds <= 0)
    return L"-";
  std::tm t{};
  const time_t tt = (time_t)unixSeconds;
  if (localtime_s(&t, &tt) != 0)
    return L"-";
  wchar_t b[40];
  swprintf(b, 40, L"%04d-%02d-%02d %02d:%02d", t.tm_year + 1900, t.tm_mon + 1, t.tm_mday,
           t.tm_hour, t.tm_min);
  return b;
}

std::wstring ComboText(HWND combo, int index)
{
  if (index < 0)
    return std::wstring();
  wchar_t buf[256];
  const int n = (int)::SendMessageW(combo, CB_GETLBTEXT, index, (LPARAM)buf);
  return n >= 0 ? std::wstring(buf, (size_t)n) : std::wstring();
}

void SetStatus(const std::wstring &s) { SetText(g.Status, s); }

void SetControlsEnabled(bool on)
{
  for (int id : { ID_BTN_SRC_FILE, ID_BTN_SRC_DIR, ID_COMBO_FMT, ID_COMBO_METHOD,
                  ID_COMBO_LEVEL, ID_CHK_RECURSE, ID_CHK_ENCHDR, ID_CHK_SOLID,
                  ID_BTN_COMPRESS, ID_CHK_ROUNDTRIP, ID_BTN_ARC, ID_BTN_DEST,
                  ID_CHK_TESTONLY, ID_BTN_LIST, ID_BTN_EXTRACT, ID_BTN_SELFTEST,
                  ID_BTN_FORMATS, ID_BTN_CODECS })
    ::EnableWindow(::GetDlgItem(g.Main, id), on ? TRUE : FALSE);
}

// ------------------------------------------------------------------ list view

void ListClear(int columns, const wchar_t *titles[], const int widths[])
{
  ListView_DeleteAllItems(g.List);
  // DeleteColumn returns FALSE once none are left; there is no portable
  // column-count macro, so drain them instead of asking how many there are.
  while (ListView_DeleteColumn(g.List, 0))
    ;
  for (int i = 0; i < columns; i++)
  {
    LVCOLUMNW col{};
    col.mask = LVCF_TEXT | LVCF_WIDTH | LVCF_SUBITEM;
    col.pszText = (LPWSTR)titles[i];
    col.cx = widths[i];
    col.iSubItem = i;
    ListView_InsertColumn(g.List, i, &col);
  }
}

void ListAddRow(int at, const std::vector<std::wstring> &cells)
{
  LVITEMW item{};
  item.mask = LVIF_TEXT;
  item.iItem = at;
  item.iSubItem = 0;
  item.pszText = (LPWSTR)cells[0].c_str();
  const int row = ListView_InsertItem(g.List, &item);
  for (size_t c = 1; c < cells.size(); c++)
    ListView_SetItemText(g.List, row, (INT)c, (LPWSTR)cells[c].c_str());
}

void ShowEntries(const std::vector<azgui::Entry> &entries)
{
  const wchar_t *titles[] = { L"#", L"名称", L"方法", L"原始大小", L"压缩后", L"CRC", L"属性", L"修改时间" };
  // Sum must stay inside the 836px list width or the last column needs scrolling.
  const int widths[] = { 42, 224, 92, 96, 92, 82, 84, 116 };
  ListClear(8, titles, widths);
  int row = 0;
  for (const auto &e : entries)
  {
    std::wstring attrs = e.isDir ? L"目录" : L"文件";
    if (e.encrypted)
      attrs += L" 加密";
    ListAddRow(row++, {
      std::to_wstring(e.index),
      e.name,
      e.method.empty() ? L"-" : e.method,
      e.hasSize ? WithCommas(e.size) : L"-",
      e.hasPackedSize ? WithCommas(e.packedSize) : L"-",
      e.crc ? HexU32(e.crc) : L"-",
      attrs,
      FormatMtime(e.mtime),
    });
  }
}

void ShowLines(const std::wstring &blob)
{
  const wchar_t *titles[] = { L"结果" };
  const int widths[] = { 812 };
  ListClear(1, titles, widths);
  int row = 0;
  size_t pos = 0;
  while (pos <= blob.size())
  {
    size_t nl = blob.find_first_of(L"\r\n", pos);
    if (nl == std::wstring::npos)
    {
      if (pos < blob.size())
        ListAddRow(row++, { blob.substr(pos) });
      break;
    }
    ListAddRow(row++, { blob.substr(pos, nl - pos) });
    pos = nl;
    while (pos < blob.size() && (blob[pos] == L'\r' || blob[pos] == L'\n'))
      pos++;
  }
}

// ------------------------------------------------------------------ file picker

// One picker for files and folders: FOS_PICKFOLDERS in `options` switches modes.
std::wstring PickOpen(HWND owner, DWORD options, const wchar_t *title)
{
  IFileOpenDialog *dlg = nullptr;
  if (FAILED(::CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER,
                               IID_PPV_ARGS(&dlg))))
    return std::wstring();
  std::wstring out;
  dlg->SetOptions(options);
  dlg->SetTitle(title);
  if (SUCCEEDED(dlg->Show(owner)))
  {
    IShellItem *item = nullptr;
    if (SUCCEEDED(dlg->GetResult(&item)))
    {
      PWSTR path = nullptr;
      if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
      {
        out = path;
        ::CoTaskMemFree(path);
      }
      item->Release();
    }
  }
  dlg->Release();
  return out;
}

std::wstring PickSave(HWND owner, const std::wstring &ext, const std::wstring &description)
{
  IFileSaveDialog *dlg = nullptr;
  if (FAILED(::CoCreateInstance(CLSID_FileSaveDialog, nullptr, CLSCTX_INPROC_SERVER,
                               IID_PPV_ARGS(&dlg))))
    return std::wstring();
  std::wstring out;
  const std::wstring pattern = L"*." + ext;
  const COMDLG_FILTERSPEC spec[] = { { description.c_str(), pattern.c_str() } };
  dlg->SetFileTypes(1, spec);
  dlg->SetDefaultExtension(ext.c_str());
  dlg->SetTitle(L"选择归档输出位置");
  if (SUCCEEDED(dlg->Show(owner)))
  {
    IShellItem *item = nullptr;
    if (SUCCEEDED(dlg->GetResult(&item)))
    {
      PWSTR path = nullptr;
      if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
      {
        out = path;
        ::CoTaskMemFree(path);
      }
      item->Release();
    }
  }
  dlg->Release();
  return out;
}

// The extension a format normally writes, so the save dialog suggests a name the
// engine will accept.
std::wstring ExtensionForFormat(const std::string &format)
{
  uint32_t index = 0;
  if (az_format_find(format.c_str(), &index) != AZ_OK)
    return azgui::ToWide(format.c_str());
  az_format_info f;
  std::memset(&f, 0, sizeof(f));
  f.struct_size = sizeof(f);
  std::wstring ext = azgui::ToWide(format.c_str());
  if (az_format_get(index, &f) == AZ_OK)
  {
    if (f.extension && *f.extension)
      ext = azgui::ToWide(f.extension);
    az_format_release(&f);
  }
  return ext;
}

// ------------------------------------------------------------------ job runner

// Runs `work` on a worker thread. `work` talks to the UI only through posted
// messages, because it is not allowed to touch a control.
void StartJob(const std::wstring &label, std::function<void()> work)
{
  if (g.Busy.exchange(true))
    return;
  g.LastPosted = 0;
  SetControlsEnabled(false);
  ::SendMessageW(g.Progress, PBM_SETRANGE32, 0, 1000);
  ::SendMessageW(g.Progress, PBM_SETPOS, 0, 0);
  SetStatus(L"进行中：" + label);
  std::thread([work] {
    work();
    PostMessageW(g.Main, WM_APP + 4, 0, 0);
  }).detach();
}

#define WM_APP_DONE (WM_APP + 4)

// Progress arrives from whichever thread the coder uses; throttle it to about
// a thousand steps so a fast deflate cannot flood the queue.
azgui::ProgressFn UiProgress()
{
  return [](const azgui::Progress &p)
  {
    const unsigned long long step = p.total ? (p.total / 1000 + 1) : (1ull << 20);
    const unsigned long long last = g.LastPosted.load();
    if (p.done != 0 && p.done < last + step && p.done != p.total)
      return;
    g.LastPosted = p.done;
    PostMessageW(g.Main, WM_APP_PROGRESS, 0,
                 reinterpret_cast<LPARAM>(new azgui::Progress{ p }));
  };
}

void PostResult(Result *r)
{
  PostMessageW(g.Main, WM_APP_RESULT, 0, reinterpret_cast<LPARAM>(r));
}

// ------------------------------------------------------------------ operations

// Reads every control the job needs. Must run on the UI thread.
azgui::Job CompressJobFromUi(const std::wstring &archivePath)
{
  azgui::Job j;
  j.archivePath = archivePath;
  j.sourcePath = GetText(g.EditSrc);
  j.format = azgui::ToUtf8(ComboText(g.ComboFmt, (int)SendMessageW(g.ComboFmt, CB_GETCURSEL, 0, 0)));
  // Combo labels are "7z  (*.7z)"; the engine name is everything before that.
  const size_t cut = j.format.find_first_of(" \t(");
  if (cut != std::string::npos)
    j.format = j.format.substr(0, cut);
  const int mi = (int)SendMessageW(g.ComboMethod, CB_GETCURSEL, 0, 0);
  j.method = mi > 0 ? azgui::ToUtf8(ComboText(g.ComboMethod, mi)) : std::string();
  const int li = (int)SendMessageW(g.ComboLevel, CB_GETCURSEL, 0, 0);
  j.level = li <= 0 ? -1 : li - 1;
  j.password = GetUtf8(g.EditPassC);
  j.encryptHeaders = Checked(g.ChkEncHdr);
  j.solid = Checked(g.ChkSolid);
  j.recurse = Checked(g.ChkRecurse);
  return j;
}

void DoCompress()
{
  std::wstring archive = GetText(g.EditArc);
  if (GetText(g.EditSrc).empty())
  {
    SetStatus(L"请先用“选文件”或“选文件夹”指定要压缩的内容。");
    return;
  }
  if (archive.empty())
  {
    const std::string fmt =
        azgui::ToUtf8(ComboText(g.ComboFmt, (int)SendMessageW(g.ComboFmt, CB_GETCURSEL, 0, 0)));
    const std::string name = fmt.substr(0, fmt.find_first_of(" \t("));
    const std::wstring ext = ExtensionForFormat(name);
    archive = PickSave(g.Main, ext, L"归档文件");
    if (archive.empty())
    {
      SetStatus(L"已取消。");
      return;
    }
    SetText(g.EditArc, archive);
  }

  azgui::Job j = CompressJobFromUi(archive);
  const bool verify = Checked(g.ChkRoundTrip);

  StartJob(L"压缩 → " + archive, [j, verify] {
    std::wstring err, report;
    bool ok = azgui::Compress(j, UiProgress(), err);
    if (ok && verify)
      ok = azgui::RoundTripFile(j, report, err);
    auto *r = new Result();
    r->ok = ok;
    r->kind = (ok && !report.empty()) ? RESULT_LINES : RESULT_STATUS;
    if (ok)
      r->text = report.empty() ? (L"压缩完成：" + j.archivePath) : report;
    else
      r->text = L"压缩失败：" + (err.empty() ? L"未知错误" : err);
    PostResult(r);
  });
}

void DoList()
{
  const std::wstring arc = GetText(g.EditArc);
  const std::string pass = GetUtf8(g.EditPassX);
  if (arc.empty())
  {
    SetStatus(L"请先在“归档文件”里选择要打开的归档。");
    return;
  }
  StartJob(L"读取条目 ← " + arc, [arc, pass] {
    std::wstring err;
    auto *r = new Result();
    r->entries = azgui::ListEntries(arc, pass, err);
    r->ok = !r->entries.empty();
    r->kind = RESULT_ENTRIES;
    r->text = r->ok ? (L"条目数：" + WithCommas(r->entries.size()) + L"   " + arc)
                    : (err.empty() ? L"归档里没有条目" : L"打开失败：" + err);
    if (!r->ok)
      r->kind = RESULT_STATUS;
    PostResult(r);
  });
}

void DoExtract()
{
  const std::wstring arc = GetText(g.EditArc);
  const std::wstring dest = GetText(g.EditDest);
  const std::string pass = GetUtf8(g.EditPassX);
  const bool testOnly = Checked(g.ChkTestOnly);
  if (arc.empty())
  {
    SetStatus(L"请先在“归档文件”里选择要打开的归档。");
    return;
  }
  if (!testOnly && dest.empty())
  {
    SetStatus(L"请先选择解压目标目录（或勾选“只校验 CRC 不解压”）。");
    return;
  }
  StartJob(testOnly ? (L"完整性校验 ← " + arc) : (L"解压 → " + dest),
           [arc, dest, pass, testOnly] {
             std::wstring err;
             const bool ok = azgui::Extract(arc, testOnly ? L"" : dest, pass, testOnly,
                                            UiProgress(), err);
             auto *r = new Result();
             r->ok = ok;
             r->kind = RESULT_STATUS;
             if (ok)
             {
               r->text = testOnly ? (L"校验通过：" + arc) : (L"解压完成：" + dest);
               // AZ_OK with a message is the ABI's "succeeded, but note this".
               const std::wstring note = azgui::ToWide(az_last_error_message());
               if (!note.empty())
                 r->text += L"   [提示] " + note;
             }
             else
               r->text = (testOnly ? L"校验失败：" : L"解压失败：") + err;
             PostResult(r);
           });
}

void DoSelfTest()
{
  StartJob(L"内置自检", [] {
    std::wstring report, err;
    const bool ok = azgui::SelfTest(report, err);
    auto *r = new Result();
    r->ok = ok;
    r->kind = RESULT_LINES;
    r->text = report;
    PostResult(r);
  });
}

std::wstring JoinLines(const std::vector<std::wstring> &rows)
{
  std::wstring s;
  for (const auto &r : rows)
    s += r + L"\r\n";
  return s;
}

void DoShowFormats()
{
  const auto rows = azgui::ListAllFormats();
  ShowLines(JoinLines(rows));
  SetStatus(L"引擎可读格式 " + WithCommas(rows.size()) +
            L" 个（read=可读取  write=可创建）");
}

void DoShowCodecs()
{
  const auto rows = azgui::ListAllCodecs();
  ShowLines(JoinLines(rows));
  SetStatus(L"编解码器 " + WithCommas(rows.size()) + L" 个（enc=可编码  dec=可解码）");
}

// ------------------------------------------------------------------ combo fill

void FillMethods();

void FillFormats()
{
  SendMessageW(g.ComboFmt, CB_RESETCONTENT, 0, 0);
  for (const auto &pair : azgui::WritableFormats())
    SendMessageW(g.ComboFmt, CB_ADDSTRING, 0, (LPARAM)pair.first.c_str());
  if (SendMessageW(g.ComboFmt, CB_GETCOUNT, 0, 0) > 0)
    SendMessageW(g.ComboFmt, CB_SETCURSEL, 0, 0);
  FillMethods();
}

void FillMethods()
{
  const std::wstring label = ComboText(g.ComboFmt, (int)SendMessageW(g.ComboFmt, CB_GETCURSEL, 0, 0));
  const size_t cut = label.find_first_of(L" \t(");
  const std::string format = azgui::ToUtf8(cut == std::wstring::npos ? label : label.substr(0, cut));

  SendMessageW(g.ComboMethod, CB_RESETCONTENT, 0, 0);
  SendMessageW(g.ComboMethod, CB_ADDSTRING, 0, (LPARAM)L"（使用默认）");
  for (const auto &m : azgui::MethodsFor(format))
    SendMessageW(g.ComboMethod, CB_ADDSTRING, 0, (LPARAM)azgui::ToWide(m.c_str()).c_str());
  SendMessageW(g.ComboMethod, CB_SETCURSEL, 0, 0);
}

// ------------------------------------------------------------------ window proc

void OnCommand(WPARAM wp)
{
  const int id = LOWORD(wp);
  if (id == ID_COMBO_FMT && HIWORD(wp) == CBN_SELCHANGE)
  {
    FillMethods();
    return;
  }
  switch (id)
  {
    case ID_BTN_SRC_FILE:
    {
      auto p = PickOpen(g.Main, FOS_FORCEFILESYSTEM, L"选择要压缩的文件");
      if (!p.empty())
        SetText(g.EditSrc, p);
      break;
    }
    case ID_BTN_SRC_DIR:
    {
      auto p = PickOpen(g.Main, FOS_FORCEFILESYSTEM | FOS_PICKFOLDERS, L"选择要压缩的文件夹");
      if (!p.empty())
        SetText(g.EditSrc, p);
      break;
    }
    case ID_BTN_ARC:
    {
      auto p = PickOpen(g.Main, FOS_FORCEFILESYSTEM, L"选择归档文件");
      if (p.empty())
        break;
      SetText(g.EditArc, p);
      if (GetText(g.EditDest).empty())
      {
        // Suggest a sibling folder, but only if nothing of that name is
        // already there -- do not point the user at a directory they have data in.
        const fs::path arc(p);
        const fs::path suggested = arc.parent_path() / arc.stem();
        std::error_code ec;
        if (!fs::exists(suggested, ec))
          SetText(g.EditDest, suggested.wstring());
      }
      break;
    }
    case ID_BTN_DEST:
    {
      auto p = PickOpen(g.Main, FOS_FORCEFILESYSTEM | FOS_PICKFOLDERS, L"选择解压目标目录");
      if (!p.empty())
        SetText(g.EditDest, p);
      break;
    }
    case ID_BTN_COMPRESS: DoCompress(); break;
    case ID_BTN_LIST:     DoList(); break;
    case ID_BTN_EXTRACT:  DoExtract(); break;
    case ID_BTN_SELFTEST: DoSelfTest(); break;
    case ID_BTN_FORMATS:  DoShowFormats(); break;
    case ID_BTN_CODECS:   DoShowCodecs(); break;
  }
}

LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp)
{
  switch (msg)
  {
    case WM_CREATE:
    {
      HFONT font = (HFONT)::GetStockObject(DEFAULT_GUI_FONT);
      // Every control is a direct child of the main window; the group boxes are
      // just frames drawn behind them, which keeps the coordinates single-system.
      // `id` defaults to 0 because the decorative controls (group boxes, labels)
      // never get a notification.
      auto Create = [&](LPCWSTR cls, LPCWSTR text, DWORD style, int x, int y, int w, int ht,
                        int id = 0) -> HWND
      {
        HWND c = ::CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style, x, y, w, ht, h,
                                   (HMENU)(INT_PTR)id, nullptr, nullptr);
        ::SendMessageW(c, WM_SETFONT, (WPARAM)font, TRUE);
        return c;
      };
      auto Static = [&](LPCWSTR t, int x, int y, int w) {
        Create(L"STATIC", t, SS_LEFTNOWORDWRAP, x, y, w, 18);
      };
      auto Group = [&](LPCWSTR t, int x, int y, int w, int ht) {
        Create(L"BUTTON", t, BS_GROUPBOX, x, y, w, ht);
      };
      auto Button = [&](LPCWSTR t, int x, int y, int w, int ht, int id) {
        return Create(L"BUTTON", t, BS_PUSHBUTTON, x, y, w, ht, id);
      };
      auto Check = [&](LPCWSTR t, int x, int y, int w, int id, bool on) {
        HWND c = Create(L"BUTTON", t, BS_AUTOCHECKBOX, x, y, w, 22, id);
        Button_SetCheck(c, on ? BST_CHECKED : BST_UNCHECKED);
        return c;
      };
      auto Edit = [&](int x, int y, int w, int id, DWORD extra = 0) {
        return Create(L"EDIT", L"", ES_AUTOHSCROLL | extra, x, y, w, 22, id);
      };
      auto Combo = [&](int x, int y, int w, int id) {
        return Create(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_VSCROLL, x, y, w, 260, id);
      };

      Group(L"压缩", 8, 6, 424, 292);
      Static(L"源文件 / 文件夹", 20, 30, 200);
      g.EditSrc = Edit(20, 50, 250, ID_EDIT_SRC);
      Button(L"选文件", 278, 49, 72, 24, ID_BTN_SRC_FILE);
      Button(L"选文件夹", 278, 78, 72, 24, ID_BTN_SRC_DIR);
      Static(L"格式", 20, 84, 60);
      g.ComboFmt = Combo(20, 104, 152, ID_COMBO_FMT);
      Static(L"压缩方法", 182, 84, 90);
      g.ComboMethod = Combo(182, 104, 168, ID_COMBO_METHOD);
      Static(L"级别", 20, 138, 60);
      g.ComboLevel = Combo(20, 158, 100, ID_COMBO_LEVEL);
      Static(L"口令（可留空）", 132, 138, 160);
      g.EditPassC = Edit(132, 158, 218, ID_EDIT_PASSC, ES_PASSWORD);
      g.ChkRecurse = Check(L"递归子目录", 20, 190, 108, ID_CHK_RECURSE, true);
      g.ChkSolid = Check(L"固实块", 132, 190, 84, ID_CHK_SOLID, true);
      g.ChkEncHdr = Check(L"加密文件名", 220, 190, 118, ID_CHK_ENCHDR, false);
      Button(L"开始压缩", 20, 220, 150, 32, ID_BTN_COMPRESS);
      g.ChkRoundTrip = Check(L"压缩后逐字节往返校验", 182, 226, 240, ID_CHK_ROUNDTRIP, true);
      Static(L"“目标归档”留空时会弹出保存对话框", 20, 262, 380);

      Group(L"解压", 444, 6, 424, 292);
      Static(L"归档文件", 456, 30, 200);
      g.EditArc = Edit(456, 50, 250, ID_EDIT_ARC);
      Button(L"选择", 714, 49, 72, 24, ID_BTN_ARC);
      Static(L"解压到目录", 456, 84, 200);
      g.EditDest = Edit(456, 104, 250, ID_EDIT_DEST);
      Button(L"选择", 714, 103, 72, 24, ID_BTN_DEST);
      Static(L"口令（可留空）", 456, 138, 160);
      g.EditPassX = Edit(456, 158, 250, ID_EDIT_PASSX, ES_PASSWORD);
      g.ChkTestOnly = Check(L"只校验 CRC", 714, 158, 120, ID_CHK_TESTONLY, false);
      Button(L"列出条目", 456, 196, 120, 32, ID_BTN_LIST);
      Button(L"开始解压", 586, 196, 120, 32, ID_BTN_EXTRACT);
      Static(L"勾选“只校验 CRC”后，“开始解压”只验完整性", 456, 240, 392);

      Group(L"条目 / 结果", 8, 306, 860, 258);
      g.List = Create(L"SysListView32", L"",
                      WS_BORDER | LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS | LVS_NOSORTHEADER,
                      20, 326, 836, 226, ID_LIST);
      ListView_SetExtendedListViewStyle(g.List, LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES);

      g.Progress = Create(PROGRESS_CLASSW, L"", PBS_SMOOTH, 8, 572, 600, 18, ID_PROGRESS);
      Button(L"自检", 620, 568, 80, 26, ID_BTN_SELFTEST);
      Button(L"格式列表", 706, 568, 80, 26, ID_BTN_FORMATS);
      Button(L"编码器", 792, 568, 72, 26, ID_BTN_CODECS);
      g.Status = Create(L"STATIC", L"准备就绪", SS_LEFTNOWORDWRAP, 8, 600, 860, 20, ID_STATUS);

      // Index 0 is the format's own default; the rest are the explicit levels.
      SendMessageW(g.ComboLevel, CB_ADDSTRING, 0, (LPARAM)L"（默认）");
      for (int i = 0; i <= 9; i++)
      {
        std::wstring t = i == 0 ? L"0（仅存储）" : std::to_wstring(i);
        SendMessageW(g.ComboLevel, CB_ADDSTRING, 0, (LPARAM)t.c_str());
      }
      SendMessageW(g.ComboLevel, CB_SETCURSEL, 0, 0);

      FillFormats();
      DoShowFormats();
      return 0;
    }

    case WM_COMMAND:
      OnCommand(wp);
      return 0;

    case WM_APP_PROGRESS:
    {
      std::unique_ptr<azgui::Progress> m(reinterpret_cast<azgui::Progress *>(lp));
      if (!m)
        return 0;
      if (m->total)
      {
        unsigned long long permille = (m->done * 1000) / m->total;
        ::SendMessageW(g.Progress, PBM_SETPOS, (WPARAM)(permille > 1000 ? 1000 : permille), 0);
      }
      std::wstring s = L"进行中  " + WithCommas(m->done);
      if (m->total)
        s += L" / " + WithCommas(m->total);
      if (!m->entry.empty())
        s += L"   " + m->entry;
      SetStatus(s);
      return 0;
    }

    case WM_APP_RESULT:
    {
      std::unique_ptr<Result> r(reinterpret_cast<Result *>(lp));
      if (!r)
        return 0;
      if (r->kind == RESULT_ENTRIES)
        ShowEntries(r->entries);
      else if (r->kind == RESULT_LINES)
        ShowLines(r->text);
      SetStatus(r->text);
      return 0;
    }

    case WM_APP_DONE:
      g.Busy = false;
      SetControlsEnabled(true);
      return 0;

    case WM_CTLCOLORSTATIC:
      ::SetBkMode((HDC)wp, TRANSPARENT);
      return (LRESULT)::GetSysColorBrush(COLOR_BTNFACE);

    case WM_CLOSE:
      if (g.Busy.load() &&
          ::MessageBoxW(h, L"还有任务正在运行，确定要退出吗？", L"Andzip",
                        MB_YESNO | MB_ICONWARNING) != IDYES)
        return 0;
      DestroyWindow(h);
      return 0;

    case WM_DESTROY:
      PostQuitMessage(0);
      return 0;
  }
  return ::DefWindowProcW(h, msg, wp, lp);
}

} // namespace

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR, int show)
{
  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);

  INITCOMMONCONTROLSEX icc{ sizeof(icc),
                            ICC_LISTVIEW_CLASSES | ICC_BAR_CLASSES | ICC_STANDARD_CLASSES |
                                ICC_USEREX_CLASSES };
  ::InitCommonControlsEx(&icc);

  // Report what the build actually is, before the user touches anything.
  const azgui::BuildInfo bi = azgui::QueryBuild();

  WNDCLASSW wc{};
  wc.lpfnWndProc = WndProc;
  wc.hInstance = inst;
  wc.hCursor = ::LoadCursor(nullptr, IDC_ARROW);
  wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
  wc.lpszClassName = L"AndzipAzGuiWindow";
  wc.hIcon = ::LoadIcon(nullptr, IDI_APPLICATION);
  ::RegisterClassW(&wc);

  std::wstring title = L"Andzip 压缩内核验证界面";
  title += bi.coreVersion.empty() ? L"（未能加载 andzip_core）"
                                  : (L"  —  " + bi.engine + L" / " + bi.config + L" / " +
                                     WithCommas(bi.formatCount) + L" 种格式 / " +
                                     WithCommas(bi.codecCount) + L" 个编解码器");

  // Non-resizable on purpose: the layout is a fixed pixel grid, and a
  // verification tool does not need to be resized.
  RECT rc{ 0, 0, 876, 640 };
  ::AdjustWindowRect(&rc, WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX, FALSE);
  g.Main = ::CreateWindowExW(0, wc.lpszClassName, title.c_str(),
                             WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
                             CW_USEDEFAULT, CW_USEDEFAULT, rc.right - rc.left, rc.bottom - rc.top,
                             nullptr, nullptr, inst, nullptr);
  if (!g.Main)
  {
    ::MessageBoxW(nullptr, L"窗口创建失败。", L"Andzip", MB_ICONERROR);
    ::CoUninitialize();
    return 1;
  }
  ::ShowWindow(g.Main, show);
  ::UpdateWindow(g.Main);

  if (bi.coreVersion.empty())
    ::MessageBoxW(g.Main,
                  L"没能加载 andzip_core.dll。\n\n请把 azgui.exe 与 andzip_core.dll 放在同一个目录，"
                  L"或设置环境变量 ANDZIP_CORE_PATH 指向该 DLL 的完整路径。",
                  L"Andzip", MB_ICONERROR);

  MSG m;
  while (::GetMessageW(&m, nullptr, 0, 0) > 0)
  {
    if (::IsDialogMessageW(g.Main, &m))
      continue;
    ::TranslateMessage(&m);
    ::DispatchMessage(&m);
  }
  ::CoUninitialize();
  return (int)m.wParam;
}
