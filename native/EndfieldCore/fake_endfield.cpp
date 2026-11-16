// The dummy Endfield.exe of the decoy tests: a GUI-subsystem program (like the real game) that records its parent
// pid, the parent's image path and a few facts into the file named by ECL_FAKE_RECORD, then lives ECL_FAKE_LIFE_MS ms.
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <filesystem>
#include <fstream>
#include <string>

#include "endfield_decoy.h"

namespace {
std::wstring Env(const wchar_t *name) {
  wchar_t buffer[2048] = {};
  const DWORD n = GetEnvironmentVariableW(name, buffer, 2048);
  return n && n < 2048 ? std::wstring(buffer, n) : std::wstring();
}
std::wstring ImagePathOf(DWORD pid) {
  std::wstring out;
  if (HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)) {
    wchar_t path[1024] = {};
    DWORD n = 1024;
    if (QueryFullProcessImageNameW(h, 0, path, &n)) out.assign(path, n);
    CloseHandle(h);
  }
  return out;
}
}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
  const std::wstring record = Env(L"ECL_FAKE_RECORD");
  const DWORD life_ms = static_cast<DWORD>(_wtoi(Env(L"ECL_FAKE_LIFE_MS").c_str()));
  const DWORD parent = endfield::RealOps().parent_pid_of(GetCurrentProcess());
  BOOL in_job = FALSE;
  IsProcessInJob(GetCurrentProcess(), nullptr, &in_job);
  if (!record.empty()) {
    const std::wstring tmp = record + L".tmp";
    {
      std::wofstream out(tmp, std::ios::binary);
      out << L"self_pid=" << GetCurrentProcessId() << L"\n";
      out << L"parent_pid=" << parent << L"\n";
      out << L"parent_image=" << ImagePathOf(parent) << L"\n";
      out << L"in_job=" << (in_job ? 1 : 0) << L"\n";
      out << L"cwd=" << std::filesystem::current_path().wstring() << L"\n";
      out << L"cmdline=" << GetCommandLineW() << L"\n";
    }
    MoveFileExW(tmp.c_str(), record.c_str(), MOVEFILE_REPLACE_EXISTING);
  }
  Sleep(life_ms ? life_ms : 60000);
  return 0;
}
