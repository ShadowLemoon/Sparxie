// Shared helpers of the EndfieldCore test programs: the CHECK / Run harness, a temp folder, the session wrapper, process and file waits, a job object.
// Header only; every test program includes it once.
#ifndef SPARXIE_ENDFIELD_TEST_SUPPORT_H
#define SPARXIE_ENDFIELD_TEST_SUPPORT_H

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <functional>
#include <sstream>
#include <string>

#include "endfield_core_abi.h"

namespace endfield_test {

namespace fs = std::filesystem;

inline int g_failed = 0;
inline int g_run = 0;

#define CHECK(cond)                                                                 \
  do {                                                                              \
    if (!(cond)) {                                                                  \
      std::fprintf(stderr, "    CHECK FAILED %s:%d: %s\n", __FILE__, __LINE__, #cond); \
      ++g_failed;                                                                   \
      failed_here = true;                                                           \
    }                                                                               \
  } while (0)

inline bool failed_here = false;

inline void Run(const char *name, const std::function<void()> &body) {
  ++g_run;
  failed_here = false;
  std::printf("[ RUN ] %s\n", name);
  std::fflush(stdout);
  try {
    body();
  } catch (const std::exception &e) {
    std::fprintf(stderr, "    EXCEPTION %s\n", e.what());
    ++g_failed;
    failed_here = true;
  }
  std::printf("[%s] %s\n", failed_here ? "FAIL" : "  OK", name);
  std::fflush(stdout);
}

struct TempDir {
  fs::path path;
  TempDir() {
    wchar_t base[MAX_PATH];
    GetTempPathW(MAX_PATH, base);
    path = fs::path(base) / (L"sparxie-endfield-test-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(path);
  }
  ~TempDir() {
    std::error_code ec;
    fs::remove_all(path, ec);
  }
};

struct Session {
  void *handle = nullptr;
  EndfieldResult result{};
  EndfieldLaunchRequest request{};
  std::wstring game_path;
};

inline Session NewSession(const fs::path &game) {
  Session s;
  s.game_path = game.wstring();
  s.request.size = sizeof(EndfieldLaunchRequest);
  s.request.abi_version = ENDFIELD_ABI_VERSION;
  s.request.fps_unlock_enabled = 1;
  s.request.target_fps = 144;
  s.request.touch_ui_enabled = 1;
  s.request.width = 1920;
  s.request.height = 1080;
  s.request.refresh_rate = 60;
  s.request.game_executable_path = s.game_path.c_str();
  s.request.game_executable_path_chars = static_cast<uint32_t>(s.game_path.size());
  s.result.size = sizeof(EndfieldResult);
  return s;
}

inline bool Alive(DWORD pid) {
  HANDLE h = OpenProcess(SYNCHRONIZE, FALSE, pid);
  if (!h) return false;
  const bool running = WaitForSingleObject(h, 0) == WAIT_TIMEOUT;
  CloseHandle(h);
  return running;
}

inline bool WaitGone(DWORD pid, DWORD ms) {
  for (DWORD waited = 0; waited <= ms; waited += 50) {
    if (!Alive(pid)) return true;
    Sleep(50);
  }
  return false;
}

inline bool WaitFile(const fs::path &p, DWORD ms) {
  for (DWORD waited = 0; waited <= ms; waited += 50) {
    if (fs::exists(p)) return true;
    Sleep(50);
  }
  return false;
}

inline std::wstring Field(const std::wstring &text, const std::wstring &key) {
  const size_t at = text.find(key + L"=");
  if (at == std::wstring::npos) return {};
  const size_t end = text.find(L'\n', at);
  return text.substr(at + key.size() + 1, end - at - key.size() - 1);
}

inline std::wstring ReadWide(const fs::path &p) {
  std::wifstream in(p, std::ios::binary);
  std::wstringstream ss;
  ss << in.rdbuf();
  return ss.str();
}

struct Job {
  HANDLE handle = nullptr;
  Job() {
    handle = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    SetInformationJobObject(handle, JobObjectExtendedLimitInformation, &limits, sizeof(limits));
  }
  void Close() {
    if (handle) CloseHandle(handle);
    handle = nullptr;
  }
  ~Job() { Close(); }
  bool Contains(DWORD pid) const {
    HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!p) return false;
    BOOL in = FALSE;
    IsProcessInJob(p, handle, &in);
    CloseHandle(p);
    return in;
  }
};

}  // namespace endfield_test

#endif
