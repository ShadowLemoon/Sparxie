#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <string>
#include <unordered_map>
#include <vector>

#include "endfield_shared.h"

// EndfieldRuntime.dll: loaded into Endfield.exe by EndfieldCore while the game is still suspended.
//
// What it does, and why it is built the way it is (the first build did both jobs once, from a thread of its own, and wrote no log, so a touch session still showed the keyboard UI):
//  - the game's input type (Beyond.DeviceInfo, Keyboard 0 / Touch 1 / Controller 2) is held at Touch for the whole session. The game sets it itself during start-up and again whenever it sees
//    keyboard or mouse input, so one call is not enough: it is read and, when it is not Touch, switched with the game's own ChangeInputType, 4 times a second.
//  - every managed call is made on the game's main thread. The Runtime posts a private window message to the Unity window every 250 ms and a WH_GETMESSAGE hook on that thread runs the
//    check when the message comes out of the queue (ChangeInputType raises onInputTypeChanged, which restyles Unity UI objects; that is main-thread work).
//  - the frame-rate target is held the same way.
//  - taps: the UI input module's pointer mode is held so that every finger keeps its own pointer record (endfield_pointer_mode.inc), the mouse messages Windows makes from touches are
//    removed before the game reads them, and the drag threshold follows the client's pixel density (endfield_drag.inc).
//  - everything it does is written to endfield-runtime.log (next to Sparxie's other logs).
// Behaviour of the game's DeviceInfo is the community's finding (Better-Endfield docs/ui/MOBILE_UI_REVERSING.md: "write the state through ChangeInputType, on the main thread");
// the code here is our own.

namespace {

using endfield_shared::SharedState;

HMODULE g_module = nullptr;
SharedState *g_shared = nullptr;
HANDLE g_log = INVALID_HANDLE_VALUE;
CRITICAL_SECTION g_log_lock;
bool g_log_lock_ready = false;
UINT g_pump_message = 0;
DWORD g_main_thread = 0;
HHOOK g_hook = nullptr;
LONG g_log_lines = 0;

void Log(const char *format, ...) {
  if (g_log == INVALID_HANDLE_VALUE || !g_log_lock_ready)
    return;
  if (InterlockedIncrement(&g_log_lines) > 9000)
    return;  // a hung loop must not fill a disk
  char text[900];
  SYSTEMTIME now;
  GetLocalTime(&now);
  int n = snprintf(text, sizeof(text), "[%04d-%02d-%02d %02d:%02d:%02d.%03d] [pid %lu tid %lu] ", now.wYear, now.wMonth, now.wDay,
                   now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, GetCurrentProcessId(), GetCurrentThreadId());
  va_list args;
  va_start(args, format);
  const int m = vsnprintf(text + n, sizeof(text) - n - 2, format, args);
  va_end(args);
  if (m > 0)
    n += (m < static_cast<int>(sizeof(text)) - n - 2) ? m : static_cast<int>(sizeof(text)) - n - 2;
  text[n++] = '\n';
  EnterCriticalSection(&g_log_lock);
  DWORD written = 0;
  WriteFile(g_log, text, static_cast<DWORD>(n), &written, nullptr);
  LeaveCriticalSection(&g_log_lock);
}

void OpenLog(const wchar_t *wanted) {
  InitializeCriticalSection(&g_log_lock);
  g_log_lock_ready = true;
  std::wstring path = wanted ? wanted : L"";
  for (int attempt = 0; attempt < 2 && g_log == INVALID_HANDLE_VALUE; ++attempt) {
    if (attempt == 1) {
      wchar_t temp[MAX_PATH] = {};
      GetTempPathW(MAX_PATH, temp);
      path = std::wstring(temp) + L"endfield-runtime.log";
    }
    if (path.empty())
      continue;
    g_log = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                        FILE_ATTRIBUTE_NORMAL, nullptr);
  }
}

// ---- the game's managed side (IL2CPP exports of GameAssembly.dll) ----
using DomainGet = void *(__cdecl *)();
using AssemblyOpen = const void *(__cdecl *)(void *, const char *);
using AssemblyImage = const void *(__cdecl *)(const void *);
using ClassFromName = void *(__cdecl *)(const void *, const char *, const char *);
using MethodFromName = const void *(__cdecl *)(void *, const char *, int);
using RuntimeInvoke = void *(__cdecl *)(const void *, void *, void **, void **);
using ObjectUnbox = void *(__cdecl *)(void *);
using ResolveIcall = void *(__cdecl *)(const char *);
using SetTargetFps = void(__cdecl *)(int);
using GetTargetFps = int(__cdecl *)();

struct Managed {
  HMODULE game_assembly = nullptr;
  HMODULE unity = nullptr;
  RuntimeInvoke runtime_invoke = nullptr;
  ObjectUnbox object_unbox = nullptr;
  const void *change_input_type = nullptr;
  const void *get_input_type = nullptr;
  ResolveIcall resolve_icall = nullptr;
  SetTargetFps set_fps = nullptr;
  GetTargetFps get_fps = nullptr;
  bool exports_logged = false;
  bool methods_logged = false;
  bool methods_searched = false;
};
Managed g_managed;

void FindModules() {
  if (!g_managed.unity)
    g_managed.unity = GetModuleHandleW(L"UnityPlayer.dll");
  if (!g_managed.game_assembly)
    g_managed.game_assembly = GetModuleHandleW(L"GameAssembly.dll");
}

void ResolveUnity() {
  // il2cpp_resolve_icall is an export of GameAssembly.dll in the real game (UnityPlayer.dll has one export, UnityMain); UnityPlayer.dll is the second place to look
  if (!g_managed.resolve_icall && g_managed.game_assembly)
    g_managed.resolve_icall = reinterpret_cast<ResolveIcall>(GetProcAddress(g_managed.game_assembly, "il2cpp_resolve_icall"));
  if (!g_managed.resolve_icall && g_managed.unity)
    g_managed.resolve_icall = reinterpret_cast<ResolveIcall>(GetProcAddress(g_managed.unity, "il2cpp_resolve_icall"));
  if (g_managed.resolve_icall && !g_managed.set_fps) {
    g_managed.set_fps = reinterpret_cast<SetTargetFps>(g_managed.resolve_icall("UnityEngine.Application::set_targetFrameRate(System.Int32)"));
    g_managed.get_fps = reinterpret_cast<GetTargetFps>(g_managed.resolve_icall("UnityEngine.Application::get_targetFrameRate()"));
    Log("Unity icalls: set_targetFrameRate=%s get_targetFrameRate=%s", g_managed.set_fps ? "found" : "MISSING", g_managed.get_fps ? "found" : "MISSING");
  }
}

// Called on the game's main thread. Looks for Beyond.DeviceInfo.{ChangeInputType(1 arg), get_inputType(0 args)}; true when ChangeInputType is there.
bool ResolveMethods() {
  if (g_managed.change_input_type)
    return true;
  HMODULE game = g_managed.game_assembly;
  if (!game)
    return false;
  auto domain_get = reinterpret_cast<DomainGet>(GetProcAddress(game, "il2cpp_domain_get"));
  auto assembly_open = reinterpret_cast<AssemblyOpen>(GetProcAddress(game, "il2cpp_domain_assembly_open"));
  auto assembly_image = reinterpret_cast<AssemblyImage>(GetProcAddress(game, "il2cpp_assembly_get_image"));
  auto class_from_name = reinterpret_cast<ClassFromName>(GetProcAddress(game, "il2cpp_class_from_name"));
  auto method_from_name = reinterpret_cast<MethodFromName>(GetProcAddress(game, "il2cpp_class_get_method_from_name"));
  g_managed.runtime_invoke = reinterpret_cast<RuntimeInvoke>(GetProcAddress(game, "il2cpp_runtime_invoke"));
  g_managed.object_unbox = reinterpret_cast<ObjectUnbox>(GetProcAddress(game, "il2cpp_object_unbox"));
  if (!g_managed.exports_logged) {
    g_managed.exports_logged = true;
    Log("GameAssembly exports: domain_get=%d assembly_open=%d assembly_get_image=%d class_from_name=%d class_get_method_from_name=%d runtime_invoke=%d object_unbox=%d",
        domain_get != nullptr, assembly_open != nullptr, assembly_image != nullptr, class_from_name != nullptr, method_from_name != nullptr,
        g_managed.runtime_invoke != nullptr, g_managed.object_unbox != nullptr);
  }
  if (!domain_get || !assembly_open || !assembly_image || !class_from_name || !method_from_name || !g_managed.runtime_invoke)
    return false;
  void *domain = domain_get();
  if (!domain)
    return false;
  const char *assemblies[] = {"Common.Beyond", "Assembly-CSharp"};
  for (const char *name : assemblies) {
    const void *assembly = assembly_open(domain, name);
    const void *image = assembly ? assembly_image(assembly) : nullptr;
    void *klass = image ? class_from_name(image, "Beyond", "DeviceInfo") : nullptr;
    if (!klass)
      continue;
    g_managed.change_input_type = method_from_name(klass, "ChangeInputType", 1);
    g_managed.get_input_type = method_from_name(klass, "get_inputType", 0);
    Log("Beyond.DeviceInfo found in %s: ChangeInputType=%s get_inputType=%s", name, g_managed.change_input_type ? "found" : "MISSING", g_managed.get_input_type ? "found" : "MISSING");
    if (g_managed.change_input_type)
      return true;
  }
  // the class under any other assembly name: walk every assembly the domain holds
  using DomainAssemblies = const void **(__cdecl *)(void *, size_t *);
  auto domain_assemblies = reinterpret_cast<DomainAssemblies>(GetProcAddress(game, "il2cpp_domain_get_assemblies"));
  if (domain_assemblies) {
    size_t count = 0;
    const void **list = domain_assemblies(domain, &count);
    for (size_t i = 0; list && i < count && i < 4096; ++i) {
      const void *image = list[i] ? assembly_image(list[i]) : nullptr;
      void *klass = image ? class_from_name(image, "Beyond", "DeviceInfo") : nullptr;
      if (!klass)
        continue;
      g_managed.change_input_type = method_from_name(klass, "ChangeInputType", 1);
      g_managed.get_input_type = method_from_name(klass, "get_inputType", 0);
      Log("Beyond.DeviceInfo found by walking %zu assemblies (#%zu): ChangeInputType=%s get_inputType=%s", count, i, g_managed.change_input_type ? "found" : "MISSING",
          g_managed.get_input_type ? "found" : "MISSING");
      if (g_managed.change_input_type)
        return true;
    }
  }
  if (!g_managed.methods_logged) {
    g_managed.methods_logged = true;
    Log("Beyond.DeviceInfo.ChangeInputType is not there yet (the game has not loaded its managed assemblies); trying again");
  }
  return false;
}

// -1 when it cannot be read
int ReadInputType() {
  if (!g_managed.get_input_type || !g_managed.runtime_invoke)
    return -1;
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(g_managed.get_input_type, nullptr, nullptr, &exception);
  if (exception || !boxed)
    return -1;
  const void *value = g_managed.object_unbox ? g_managed.object_unbox(boxed) : static_cast<const char *>(boxed) + 2 * sizeof(void *);
  return value ? *static_cast<const int32_t *>(value) : -1;
}

bool CallChangeInputType(int32_t type) {
  void *args[] = {&type};
  void *exception = nullptr;
  g_managed.runtime_invoke(g_managed.change_input_type, nullptr, args, &exception);
  return exception == nullptr;
}

// ---- the work, on the game's main thread ----
LONG g_pumps = 0;
LONG g_touch_applies = 0;
int g_last_reading = -2;
bool g_touch_notice = false;
int g_last_fps_seen = -2;

void EnsureTouch() {
  FindModules();
  if (!ResolveMethods())
    return;
  const int reading = ReadInputType();
  if (reading == 1) {
    if (g_last_reading != 1)
      Log("input type is Touch");
    g_last_reading = reading;
    return;
  }
  const LONG applies = InterlockedIncrement(&g_touch_applies);
  const bool ok = CallChangeInputType(1);
  const int after = ReadInputType();
  // every change of what the game says is logged, and the first applications; a game that flips back again and again does not fill the log
  if (applies <= 10 || reading != g_last_reading)
    Log("input type was %d, ChangeInputType(Touch) %s, now %d (application %ld)", reading, ok ? "returned" : "RAISED an exception", after, applies);
  g_last_reading = reading;
  if (!ok && !g_touch_notice) {
    g_touch_notice = true;
    Log("the managed call raised: the game may not be ready for it; trying again next tick");
  }
}

void EnsureFrameRate() {
  FindModules();
  ResolveUnity();
  if (!g_managed.set_fps || !g_shared->unlock_enabled)
    return;
  const int target = g_shared->target_fps;
  const int seen = g_managed.get_fps ? g_managed.get_fps() : -1;
  if (seen == target)
    return;
  g_managed.set_fps(target);
  if (seen != g_last_fps_seen) {
    Log("target frame rate was %d, set to %d", seen, target);
    g_last_fps_seen = seen;
  }
}

// ---- endfield-ui.ini beside the DLL, re-read about every 5 s (every 20th pump)
std::wstring g_ini_path;
std::string ReadWholeFile(const std::wstring &path) {
  if (path.empty())
    return "";
  HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE)
    return "";
  char text[2048] = {};
  DWORD got = 0;
  ReadFile(file, text, sizeof(text) - 1, &got, nullptr);
  CloseHandle(file);
  return text;
}
std::string ReadIniText() {
  if (g_ini_path.empty() && g_module) {
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(g_module, path, MAX_PATH);
    std::wstring dir = path;
    const size_t slash = dir.find_last_of(L"\\/");
    g_ini_path = (slash == std::wstring::npos ? std::wstring() : dir.substr(0, slash + 1)) + L"endfield-ui.ini";
  }
  return ReadWholeFile(g_ini_path);
}

#include "endfield_il2cpp.inc"
#include "endfield_pointer_mode.inc"

// touch_mouse=drop (default) | off: Windows makes WM_LBUTTONDOWN / WM_LBUTTONUP / WM_LBUTTONDBLCLK / WM_MOUSEMOVE from a touch (signature 0xFF5157xx in the message's extra info) although the
// touch itself reaches the game as WM_POINTER. The game's UI then sees the finger twice, once as a finger and once as a mouse that jumps to the same place. The hook turns these messages into WM_NULL
// before the game reads them; the mouse itself is not touched.
volatile LONG g_drop_touch_mouse = 1;
LONG g_dropped_mouse = 0;
bool IsTouchMadeMouse(UINT message, unsigned long extra_info) {
  return (message == WM_LBUTTONDOWN || message == WM_LBUTTONUP || message == WM_LBUTTONDBLCLK || message == WM_MOUSEMOVE) && (extra_info & 0xFFFFFF00ul) == 0xFF515700ul;
}

#include "endfield_drag.inc"
#include "endfield_ui_config.inc"

void ReadIni() {
  LONG want = 2, drop_mouse = 1;  // the defaults when a key (or the file) is missing
  std::string drag = "auto";
  UiConfig ui;
  std::string text = ReadIniText();
  for (char *line = strtok(text.data(), "\r\n"); line; line = strtok(nullptr, "\r\n")) {
    while (*line == ' ' || *line == '\t')
      ++line;
    if (_strnicmp(line, "ui_pointers=", 12) == 0) {
      const char *v = line + 12;
      want = _strnicmp(v, "off", 3) == 0 ? -1 : ((v[0] >= '0' && v[0] <= '2') ? v[0] - '0' : 2);
    } else if (_strnicmp(line, "touch_mouse=", 12) == 0) {
      drop_mouse = _strnicmp(line + 12, "off", 3) == 0 ? 0 : 1;
    } else if (_strnicmp(line, "drag_px=", 8) == 0) {
      drag = line + 8;
      while (!drag.empty() && (drag.back() == ' ' || drag.back() == '\t'))
        drag.pop_back();
    } else {
      ParseUiLine(line, ui);
    }
  }
  if (want != pm::g_want)
    Log("endfield-ui.ini: ui_pointers=%ld (-1 off, 0 SingleMouseOrPenButMultiTouchAndTrack, 1 SingleUnifiedPointer, 2 AllPointersAsIs)", want);
  pm::g_want = want;
  if (drop_mouse != g_drop_touch_mouse)
    Log("endfield-ui.ini: touch_mouse=%s (the mouse messages Windows makes from touches %s)", drop_mouse ? "drop" : "off", drop_mouse ? "are removed before the game reads them" : "are left alone");
  g_drop_touch_mouse = drop_mouse;
  if (drag != g_drag_setting)
    Log("endfield-ui.ini: drag_px=%s", drag.c_str());
  g_drag_setting = drag;
  ApplyUiConfig(ui);
}

void Pump() {
  if (!g_shared || !g_shared->alive)
    return;
  const LONG n = InterlockedIncrement(&g_pumps);
  if (n == 1)
    Log("first pump runs on the game's main thread (tid %lu)", GetCurrentThreadId());
  EnsureFrameRate();
  if (g_shared->touch_ui_enabled) {
    EnsureTouch();
    if (n == 1 || n % 20 == 1)
      ReadIni();
    EnsureDrag();
    pm::Tick();
    EnsureProfile();
  }
}

LONG g_hook_calls = 0;
volatile bool g_pumped_by_hook = false;
WNDPROC g_original_proc = nullptr;
HWND g_subclassed = nullptr;
void RunPump();
void RunPump() {
  __try {
    Pump();
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    static LONG reported = 0;
    if (InterlockedIncrement(&reported) <= 3)
      Log("the pump hit a structured exception 0x%08lx and skipped this tick", GetExceptionCode());
  }
}

// The fallback when the message hook never sees the pump message: the window's own procedure, replaced for the Runtime's one message (the window belongs to this process, so
// the replacement is allowed from any thread, and the message is handled on the window's thread like the hook would).
LRESULT CALLBACK PumpWndProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
  if (message == g_pump_message && g_pump_message != 0) {
    if (!g_pumped_by_hook)
      RunPump();
    return 0;
  }
  return CallWindowProcW(g_original_proc, window, message, wparam, lparam);
}

LRESULT CALLBACK GetMsgProc(int code, WPARAM wparam, LPARAM lparam) {
  if (code == HC_ACTION) {
    const LONG calls = InterlockedIncrement(&g_hook_calls);
    if (calls <= 3) {
      const MSG *seen = reinterpret_cast<const MSG *>(lparam);
      Log("message hook call %ld: wparam=%llu message=0x%04x (pump message is 0x%04x)", calls, static_cast<unsigned long long>(wparam), seen ? seen->message : 0u, g_pump_message);
    }
  }
  if (code == HC_ACTION && (wparam & PM_REMOVE)) {
    const MSG *message = reinterpret_cast<const MSG *>(lparam);
    pm::HoldFast();
    if (message && g_drop_touch_mouse && IsTouchMadeMouse(message->message, static_cast<unsigned long>(GetMessageExtraInfo()))) {
      if (InterlockedIncrement(&g_dropped_mouse) <= 6)
        Log("touch_mouse: removed message 0x%04x made from a touch (#%ld)", message->message, g_dropped_mouse);
      const_cast<MSG *>(message)->message = WM_NULL;
    }
    if (message && message->message == g_pump_message && g_pump_message != 0) {
      g_pumped_by_hook = true;
      RunPump();
    }
  }
  return CallNextHookEx(nullptr, code, wparam, lparam);
}

struct WindowSearch {
  DWORD pid;
  HWND window;
};
BOOL CALLBACK FindUnityWindow(HWND window, LPARAM param) {
  auto *search = reinterpret_cast<WindowSearch *>(param);
  DWORD pid = 0;
  GetWindowThreadProcessId(window, &pid);
  if (pid != search->pid)
    return TRUE;
  wchar_t name[64] = {};
  GetClassNameW(window, name, 64);
  if (wcscmp(name, L"UnityWndClass") != 0)
    return TRUE;
  search->window = window;
  return FALSE;
}

DWORD WINAPI RuntimeThread(void *) {
  const DWORD pid = GetCurrentProcessId();
  const std::wstring map_name = std::wstring(endfield_shared::kMapPrefix) + std::to_wstring(pid);
  HANDLE mapping = nullptr;
  while (!mapping) {
    mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, map_name.c_str());
    if (!mapping)
      Sleep(100);
  }
  g_shared = static_cast<SharedState *>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedState)));
  if (!g_shared || g_shared->magic != endfield_shared::kMapMagic) {
    if (g_shared)
      UnmapViewOfFile(g_shared);
    CloseHandle(mapping);
    return 1;
  }
  OpenLog(g_shared->log_path);
  Log("EndfieldRuntime attached: touch_ui=%ld unlock=%ld target_fps=%ld", g_shared->touch_ui_enabled, g_shared->unlock_enabled, g_shared->target_fps);
  g_pump_message = RegisterWindowMessageW(L"Sparxie.Endfield.Pump");

  HWND window = nullptr;
  DWORD ticks_without_window = 0;
  LONG posts_logged = 0;
  while (g_shared->alive) {
    if (!IsWindow(window)) {
      WindowSearch search{pid, nullptr};
      EnumWindows(FindUnityWindow, reinterpret_cast<LPARAM>(&search));
      window = search.window;
      if (window) {
        const DWORD thread = GetWindowThreadProcessId(window, nullptr);
        if (thread != g_main_thread) {
          if (g_hook) {
            UnhookWindowsHookEx(g_hook);
            g_hook = nullptr;
          }
          g_main_thread = thread;
          g_hook = SetWindowsHookExW(WH_GETMESSAGE, GetMsgProc, nullptr, thread);
          Log("Unity window %p on thread %lu: message hook %s (error %lu)", static_cast<void *>(window), thread, g_hook ? "installed" : "FAILED", g_hook ? 0ul : GetLastError());
        }
      }
      else if (++ticks_without_window == 600) {
        Log("no Unity window after 60 s: the game has not shown one; still waiting");
      }
    }
    if (window && g_hook) {
      const BOOL posted = PostMessageW(window, g_pump_message, 0, 0);
      ++posts_logged;
      if (!posted || posts_logged <= 2)
        Log("pump message posted: %s (error %lu), hook calls so far %ld", posted ? "ok" : "FAILED", posted ? 0ul : GetLastError(), g_hook_calls);
      // Windows Unity needs 5 to 11 s to start pumping messages (loading): the hook is given 60 s before the window procedure is replaced
      if (posts_logged == 240 && !g_pumped_by_hook && g_subclassed != window) {
        // the hook is installed but never ran: take the window's procedure instead
        SetLastError(0);
        g_original_proc = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(PumpWndProc)));
        g_subclassed = g_original_proc ? window : nullptr;
        Log("the message hook has not run after 240 posts (60 s); window procedure %s (error %lu)", g_original_proc ? "replaced" : "NOT replaced", g_original_proc ? 0ul : GetLastError());
      }
    }
    Sleep(250);
  }
  Log("session released; Runtime stops (pumps=%ld, touch applications=%ld)", g_pumps, g_touch_applies);
  if (g_hook)
    UnhookWindowsHookEx(g_hook);
  g_hook = nullptr;
  UnmapViewOfFile(g_shared);
  CloseHandle(mapping);
  return 0;
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
  if (reason == DLL_PROCESS_ATTACH) {
    g_module = instance;
    DisableThreadLibraryCalls(instance);
    HANDLE thread = CreateThread(nullptr, 0, RuntimeThread, nullptr, 0, nullptr);
    if (thread)
      CloseHandle(thread);
  }
  return TRUE;
}
