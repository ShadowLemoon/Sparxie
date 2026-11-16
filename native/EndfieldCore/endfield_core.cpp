#define WIN32_LEAN_AND_MEAN
#include "endfield_core_abi.h"

#include <Windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>

#include <TlHelp32.h>

#include "endfield_shared.h"

namespace {

using endfield_shared::kMapMagic;
using endfield_shared::kMapPrefix;
using endfield_shared::SharedState;

struct Session {
  EndfieldLaunchRequest request{};
  std::wstring executable;
  void *job = nullptr;
  HANDLE process = nullptr;
  HANDLE mapping = nullptr;
  SharedState *shared = nullptr;
  DWORD pid = 0;
  bool active = false;
};

std::wstring ErrorMessage(const wchar_t *message) {
  return message ? message : L"";
}

void SetResult(EndfieldResult *result, EndfieldError error, uint32_t stage,
               const wchar_t *message = nullptr) {
  if (!result)
    return;
  result->size = sizeof(EndfieldResult);
  result->error_code = error;
  result->stage = stage;
  result->detail = GetLastError();
  result->message = message;
  result->message_chars = message ? static_cast<uint32_t>(wcslen(message)) : 0;
}

bool ModuleLoaded(DWORD pid, const wchar_t *name) {
  HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
  if (snapshot == INVALID_HANDLE_VALUE)
    return false;
  MODULEENTRY32W entry{};
  entry.dwSize = sizeof(entry);
  bool found = false;
  for (BOOL ok = Module32FirstW(snapshot, &entry); ok && !found; ok = Module32NextW(snapshot, &entry))
    found = _wcsicmp(entry.szModule, name) == 0;
  CloseHandle(snapshot);
  return found;
}

// A line in the Runtime's own log (the file the injected Runtime writes), so that a session in which the Runtime never loaded still says what the launcher did.
void CoreLog(const std::filesystem::path &file, const char *format, ...) {
  char text[600];
  va_list args;
  va_start(args, format);
  vsnprintf(text, sizeof(text) - 2, format, args);
  va_end(args);
  SYSTEMTIME now;
  GetLocalTime(&now);
  char line[700];
  const int n = snprintf(line, sizeof(line), "[%04d-%02d-%02d %02d:%02d:%02d.%03d] [launcher] %s\n", now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
                         now.wMilliseconds, text);
  HANDLE log = CreateFileW(file.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (log == INVALID_HANDLE_VALUE)
    return;
  DWORD written = 0;
  WriteFile(log, line, static_cast<DWORD>(n), &written, nullptr);
  CloseHandle(log);
}

bool InjectRuntime(HANDLE process, const std::wstring &path) {
  const SIZE_T bytes = (path.size() + 1) * sizeof(wchar_t);
  void *remote = VirtualAllocEx(process, nullptr, bytes,
                                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
  if (!remote)
    return false;
  SIZE_T written = 0;
  const bool copied =
      WriteProcessMemory(process, remote, path.c_str(), bytes, &written) &&
      written == bytes;
  HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
  auto load_library = kernel ? GetProcAddress(kernel, "LoadLibraryW") : nullptr;
  HANDLE thread =
      copied && load_library
          ? CreateRemoteThread(
                process, nullptr, 0,
                reinterpret_cast<LPTHREAD_START_ROUTINE>(load_library), remote,
                0, nullptr)
          : nullptr;
  bool loaded = false;
  if (thread) {
    loaded = WaitForSingleObject(thread, 10000) == WAIT_OBJECT_0;
    DWORD exit_code = 0;
    loaded = loaded && GetExitCodeThread(thread, &exit_code) && exit_code != 0;
    CloseHandle(thread);
  }
  VirtualFreeEx(process, remote, 0, MEM_RELEASE);
  return loaded;
}

std::filesystem::path ModuleDirectory() {
  HMODULE module = nullptr;
  if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                              GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                          reinterpret_cast<LPCWSTR>(&ModuleDirectory),
                          &module)) {
    return {};
  }
  wchar_t path[MAX_PATH] = {};
  const DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
  if (!length)
    return {};
  return std::filesystem::path(path).parent_path();
}

// Where the Runtime writes its log: SPARXIE_DATA_DIR\\logs when the environment names a data folder, else the folder above app-<version> (Sparxie's own data folder
// in the portable layout, next to broker-*.log and host-*.log).
std::filesystem::path RuntimeLogPath() {
  wchar_t data[MAX_PATH * 2] = {};
  const DWORD n = GetEnvironmentVariableW(L"SPARXIE_DATA_DIR", data, MAX_PATH * 2);
  std::filesystem::path base = n && n < MAX_PATH * 2 ? std::filesystem::path(std::wstring(data, n)) : ModuleDirectory().parent_path();
  base /= L"logs";
  std::error_code ignored;
  std::filesystem::create_directories(base, ignored);
  return base / L"endfield-runtime.log";
}

std::wstring BuildCommandLine(const Session &session) {
  std::wstring command = L"\"" + session.executable + L"\"";
  command += L" -screen-width " + std::to_wstring(session.request.width);
  command += L" -screen-height " + std::to_wstring(session.request.height);
  command +=
      L" -screen-refresh-rate " + std::to_wstring(session.request.refresh_rate);
  command += L" -screen-fullscreen 1";
  return command;
}

void CloseSessionHandles(Session &session) {
  if (session.shared)
    UnmapViewOfFile(session.shared);
  if (session.mapping)
    CloseHandle(session.mapping);
  if (session.process)
    CloseHandle(session.process);
  session.shared = nullptr;
  session.mapping = nullptr;
  session.process = nullptr;
}

} // namespace

extern "C" {

ENDFIELD_API EndfieldError
endfield_create_session(const EndfieldLaunchRequest *request,
                        EndfieldResult *result, void **session_out) {
  if (!request || !result || !session_out) {
    SetResult(result, ENDFIELD_ERR_INVALID_ARGUMENT, ENDFIELD_STAGE_VALIDATION,
              L"缺少启动请求");
    return ENDFIELD_ERR_INVALID_ARGUMENT;
  }
  if (request->size < sizeof(EndfieldLaunchRequest) ||
      request->abi_version != ENDFIELD_ABI_VERSION) {
    SetResult(result, ENDFIELD_ERR_ABI_MISMATCH, ENDFIELD_STAGE_VALIDATION,
              L"Endfield ABI 不匹配");
    return ENDFIELD_ERR_ABI_MISMATCH;
  }
  if (!request->game_executable_path ||
      request->game_executable_path_chars == 0 || request->target_fps < 10 ||
      request->target_fps > 1000 || request->width < 320 ||
      request->width > 7680 || request->height < 240 ||
      request->height > 4320 || request->refresh_rate < 24 ||
      request->refresh_rate > 1000 ||
      (request->fps_unlock_enabled == 0 && request->touch_ui_enabled == 0)) {
    SetResult(result, ENDFIELD_ERR_CONFIG_INVALID, ENDFIELD_STAGE_VALIDATION,
              L"Endfield 启动设置非法");
    return ENDFIELD_ERR_CONFIG_INVALID;
  }
  auto *session = new Session();
  session->request = *request;
  session->executable.assign(request->game_executable_path,
                             request->game_executable_path_chars);
  session->active = true;
  *session_out = session;
  SetResult(result, ENDFIELD_OK, ENDFIELD_STAGE_VALIDATION);
  return ENDFIELD_OK;
}

ENDFIELD_API EndfieldError endfield_attach_job(void *handle, void *job_handle,
                                               EndfieldResult *result) {
  auto *session = static_cast<Session *>(handle);
  if (!session || !session->active) {
    SetResult(result, ENDFIELD_ERR_SESSION_NOT_ACTIVE,
              ENDFIELD_STAGE_VALIDATION, L"Endfield 会话未激活");
    return ENDFIELD_ERR_SESSION_NOT_ACTIVE;
  }
  session->job = job_handle;
  SetResult(result, ENDFIELD_OK, ENDFIELD_STAGE_VALIDATION);
  return ENDFIELD_OK;
}

ENDFIELD_API EndfieldError endfield_launch(void *handle,
                                           EndfieldResult *result) {
  auto *session = static_cast<Session *>(handle);
  if (!session || !session->active) {
    SetResult(result, ENDFIELD_ERR_SESSION_NOT_ACTIVE,
              ENDFIELD_STAGE_VALIDATION, L"Endfield 会话未激活");
    return ENDFIELD_ERR_SESSION_NOT_ACTIVE;
  }

  const auto game_path = std::filesystem::path(session->executable);
  if (!std::filesystem::is_regular_file(game_path)) {
    SetResult(result, ENDFIELD_ERR_PROCESS_NOT_FOUND, ENDFIELD_STAGE_VALIDATION,
              L"Endfield.exe 不存在");
    return ENDFIELD_ERR_PROCESS_NOT_FOUND;
  }

  std::wstring command = BuildCommandLine(*session);
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process_info{};
  const auto working_directory = game_path.parent_path().wstring();
  if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE,
                      CREATE_SUSPENDED, nullptr, working_directory.c_str(),
                      &startup, &process_info)) {
    SetResult(result, ENDFIELD_ERR_PROCESS_NOT_FOUND, ENDFIELD_STAGE_VALIDATION,
              L"无法创建 Endfield.exe");
    return ENDFIELD_ERR_PROCESS_NOT_FOUND;
  }
  session->process = process_info.hProcess;
  session->pid = process_info.dwProcessId;

  if (session->job &&
      !AssignProcessToJobObject(static_cast<HANDLE>(session->job),
                                session->process)) {
    TerminateProcess(session->process, 1);
    WaitForSingleObject(session->process, 5000);
    SetResult(result, ENDFIELD_ERR_INTERNAL, ENDFIELD_STAGE_PATCH,
              L"无法加入会话 Job");
    return ENDFIELD_ERR_INTERNAL;
  }

  const auto game_assembly = game_path.parent_path() / L"GameAssembly.dll";
  if (!std::filesystem::is_regular_file(game_assembly)) {
    TerminateProcess(session->process, 1);
    WaitForSingleObject(session->process, 5000);
    CloseHandle(process_info.hThread);
    SetResult(result, ENDFIELD_ERR_SCAN_FAILED, ENDFIELD_STAGE_SCAN_TOUCH,
              L"未找到 GameAssembly.dll，未执行 Endfield 扫描");
    return ENDFIELD_ERR_SCAN_FAILED;
  }

  std::wstring map_name = kMapPrefix + std::to_wstring(session->pid);
  session->mapping =
      CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                         sizeof(SharedState), map_name.c_str());
  session->shared = session->mapping
                        ? static_cast<SharedState *>(MapViewOfFile(
                              session->mapping, FILE_MAP_ALL_ACCESS, 0, 0,
                              sizeof(SharedState)))
                        : nullptr;
  if (!session->shared) {
    TerminateProcess(session->process, 1);
    WaitForSingleObject(session->process, 5000);
    CloseHandle(process_info.hThread);
    SetResult(result, ENDFIELD_ERR_INTERNAL, ENDFIELD_STAGE_PATCH,
              L"无法建立 Endfield 会话共享状态");
    return ENDFIELD_ERR_INTERNAL;
  }
  session->shared->magic = kMapMagic;
  session->shared->sequence = 1;
  session->shared->alive = 1;
  session->shared->unlock_enabled = session->request.fps_unlock_enabled;
  session->shared->touch_ui_enabled = session->request.touch_ui_enabled;
  session->shared->target_fps = session->request.target_fps;
  {
    const std::wstring log_path = RuntimeLogPath().wstring();
    wcsncpy_s(session->shared->log_path, endfield_shared::kLogPathChars, log_path.c_str(), _TRUNCATE);
  }

  // The Runtime is loaded after the game's first thread has started, the order Better-Endfield's loader uses for its bootstrap (create suspended, resume, then a remote LoadLibraryW):
  // a thread created in a process whose loader has not run yet starts that initialisation on its own, before the game's anti-cheat module and Unity are up, which is the
  // one place this build could not show its Runtime ever being loaded in the real game. The Runtime waits for the Unity window itself, so the late load costs nothing.
  const auto runtime = ModuleDirectory() / L"EndfieldRuntime.dll";
  const auto log_file = RuntimeLogPath();
  if (!std::filesystem::is_regular_file(runtime)) {
    TerminateProcess(session->process, 1);
    WaitForSingleObject(session->process, 5000);
    CloseHandle(process_info.hThread);
    SetResult(result, ENDFIELD_ERR_PATCH_FAILED, ENDFIELD_STAGE_PATCH,
              L"Endfield Runtime 注入失败");
    return ENDFIELD_ERR_PATCH_FAILED;
  }
  ResumeThread(process_info.hThread);
  CloseHandle(process_info.hThread);
  CoreLog(log_file, "game pid %lu resumed; waiting for its loader before loading %ls", session->pid, runtime.filename().c_str());
  // kernel32 is mapped into the process once its loader has run; a process that exits at once (or never gets there) ends the wait
  bool ready = false;
  const DWORD wait_start = GetTickCount();
  while (GetTickCount() - wait_start < 20000) {
    if (WaitForSingleObject(session->process, 0) == WAIT_OBJECT_0)
      break;
    if (ModuleLoaded(session->pid, L"kernel32.dll")) {
      ready = true;
      break;
    }
    Sleep(25);
  }
  bool injected = false;
  if (ready) {
    for (int attempt = 1; attempt <= 5 && !injected; ++attempt) {
      injected = InjectRuntime(session->process, runtime.wstring());
      CoreLog(log_file, "Runtime load attempt %d into pid %lu: %s (after %lu ms, last error %lu)", attempt, session->pid, injected ? "loaded" : "failed",
              GetTickCount() - wait_start, injected ? 0ul : GetLastError());
      if (!injected)
        Sleep(200);
    }
  } else {
    CoreLog(log_file, "the game's loader did not come up within 20 s (or the game ended); the Runtime was not loaded");
  }
  if (!injected) {
    TerminateProcess(session->process, 1);
    WaitForSingleObject(session->process, 5000);
    SetResult(result, ENDFIELD_ERR_PATCH_FAILED, ENDFIELD_STAGE_PATCH, L"Endfield Runtime 注入失败");
    return ENDFIELD_ERR_PATCH_FAILED;
  }
  SetResult(result, ENDFIELD_OK, ENDFIELD_STAGE_PATCH);
  return ENDFIELD_OK;
}

ENDFIELD_API EndfieldError endfield_set_target_fps(void *handle,
                                                   int32_t target_fps,
                                                   EndfieldResult *result) {
  auto *session = static_cast<Session *>(handle);
  if (!session || !session->active || !session->shared || target_fps < 10 ||
      target_fps > 1000) {
    SetResult(result, ENDFIELD_ERR_INVALID_ARGUMENT, ENDFIELD_STAGE_VALIDATION,
              L"Endfield target FPS 非法");
    return ENDFIELD_ERR_INVALID_ARGUMENT;
  }
  InterlockedExchange(&session->shared->target_fps, target_fps);
  InterlockedIncrement(&session->shared->sequence);
  SetResult(result, ENDFIELD_OK, ENDFIELD_STAGE_PATCH);
  return ENDFIELD_OK;
}

ENDFIELD_API EndfieldError endfield_wait_game_exit(void *handle,
                                                   uint32_t timeout_ms,
                                                   EndfieldResult *result) {
  auto *session = static_cast<Session *>(handle);
  if (!session || !session->active || !session->process) {
    SetResult(result, ENDFIELD_ERR_SESSION_NOT_ACTIVE, ENDFIELD_STAGE_WAIT,
              L"Endfield 会话未启动");
    return ENDFIELD_ERR_SESSION_NOT_ACTIVE;
  }
  const DWORD wait = WaitForSingleObject(
      session->process, timeout_ms == 0 ? INFINITE : timeout_ms);
  if (wait == WAIT_OBJECT_0) {
    SetResult(result, ENDFIELD_OK, ENDFIELD_STAGE_WAIT);
    return ENDFIELD_OK;
  }
  SetResult(result, ENDFIELD_ERR_NOT_SUPPORTED, ENDFIELD_STAGE_WAIT,
            L"等待超时");
  return ENDFIELD_ERR_NOT_SUPPORTED;
}

ENDFIELD_API EndfieldError endfield_release(void *handle) {
  auto *session = static_cast<Session *>(handle);
  if (!session)
    return ENDFIELD_ERR_INVALID_ARGUMENT;
  if (session->shared)
    InterlockedExchange(&session->shared->alive, 0);
  if (session->process) {
    DWORD code = STILL_ACTIVE;
    if (GetExitCodeProcess(session->process, &code) && code == STILL_ACTIVE) {
      // Running sessions are intentionally left alive; only the mapping closes.
    }
  }
  CloseSessionHandles(*session);
  session->active = false;
  delete session;
  return ENDFIELD_OK;
}

} // extern "C"
