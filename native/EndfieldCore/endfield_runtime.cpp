#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <exception>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "endfield_shared.h"

// EndfieldRuntime.dll: loaded into Endfield.exe by EndfieldCore while the game
// is still suspended.
//
// What it does, and why it is built the way it is (the first build did both
// jobs once, from a thread of its own, and wrote no log: the owner's phone
// still got the keyboard UI):
//  - the game's input type (Beyond.DeviceInfo, Keyboard 0 / Touch 1 /
//  Controller 2) is held at Touch for the whole session. The game sets it
//  itself during start-up and again whenever it sees
//    keyboard or mouse input, so one call is not enough: it is read and, when
//    it is not Touch, switched with the game's own ChangeInputType, 4 times a
//    second.
//  - every managed call is made on the game's main thread. The Runtime posts a
//  private window message to the Unity window every 250 ms and a WH_GETMESSAGE
//  hook on that thread runs the
//    check when the message comes out of the queue (ChangeInputType raises
//    onInputTypeChanged, which restyles Unity UI objects; that is main-thread
//    work).
//  - the frame-rate target is held the same way.
//  - everything it does is written to endfield-runtime.log (next to Sparxie's
//  other logs).
// Behaviour of the game's DeviceInfo is the community's finding
// (Better-Endfield docs/ui/MOBILE_UI_REVERSING.md: "write the state through
// ChangeInputType, on the main thread"); the code here is our own.

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
HWND g_game_window = nullptr;
LONG g_log_lines = 0;

void Log(const char *format, ...) {
  if (g_log == INVALID_HANDLE_VALUE || !g_log_lock_ready)
    return;
  if (InterlockedIncrement(&g_log_lines) > 9000)
    return; // a hung loop must not fill a disk
  char text[900];
  SYSTEMTIME now;
  GetLocalTime(&now);
  int n = snprintf(text, sizeof(text),
                   "[%04d-%02d-%02d %02d:%02d:%02d.%03d] [pid %lu tid %lu] ",
                   now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute,
                   now.wSecond, now.wMilliseconds, GetCurrentProcessId(),
                   GetCurrentThreadId());
  va_list args;
  va_start(args, format);
  const int m = vsnprintf(text + n, sizeof(text) - n - 2, format, args);
  va_end(args);
  if (m > 0)
    n += (m < static_cast<int>(sizeof(text)) - n - 2)
             ? m
             : static_cast<int>(sizeof(text)) - n - 2;
  text[n++] = '\n';
  EnterCriticalSection(&g_log_lock);
  DWORD written = 0;
  WriteFile(g_log, text, static_cast<DWORD>(n), &written, nullptr);
  LeaveCriticalSection(&g_log_lock);
}

volatile LONG g_pm_want =
    2; // ui_pointers=all|0|1|2|off: the pointerBehavior the UI input module is
       // held at (2 = AllPointersAsIs); -1 = off
volatile LONG g_pm_guard = 1; // pointer_guard=on|off: the module's setter drops
                              // changes away from the held value
volatile LONG g_pm_probe =
    0; // pointer_probe=on|off: log who changes the value (call sites, callers)
volatile LONG g_drop_touch_mouse =
    1; // touch_mouse=drop (default) | off: the mouse messages Windows makes
       // from a touch are taken out of the game thread's queue
LONG g_dropped_mouse = 0;

void OpenLog(const wchar_t *wanted) {
  InitializeCriticalSection(&g_log_lock);
  g_log_lock_ready = true;
  std::wstring path = wanted ? wanted : L"";
  for (int attempt = 0; attempt < 2 && g_log == INVALID_HANDLE_VALUE;
       ++attempt) {
    if (attempt == 1) {
      wchar_t temp[MAX_PATH] = {};
      GetTempPathW(MAX_PATH, temp);
      path = std::wstring(temp) + L"endfield-runtime.log";
    }
    if (path.empty())
      continue;
    g_log = CreateFileW(path.c_str(), FILE_APPEND_DATA,
                        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  }
}

// ---- the game's managed side (IL2CPP exports of GameAssembly.dll) ----
using DomainGet = void *(__cdecl *)();
using AssemblyOpen = const void *(__cdecl *)(void *, const char *);
using AssemblyImage = const void *(__cdecl *)(const void *);
using ClassFromName = void *(__cdecl *)(const void *, const char *,
                                        const char *);
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
  // il2cpp_resolve_icall is an export of GameAssembly.dll in the real game
  // (UnityPlayer.dll has one export, UnityMain); UnityPlayer.dll is the second
  // place to look
  if (!g_managed.resolve_icall && g_managed.game_assembly)
    g_managed.resolve_icall = reinterpret_cast<ResolveIcall>(
        GetProcAddress(g_managed.game_assembly, "il2cpp_resolve_icall"));
  if (!g_managed.resolve_icall && g_managed.unity)
    g_managed.resolve_icall = reinterpret_cast<ResolveIcall>(
        GetProcAddress(g_managed.unity, "il2cpp_resolve_icall"));
  if (g_managed.resolve_icall && !g_managed.set_fps) {
    g_managed.set_fps = reinterpret_cast<SetTargetFps>(g_managed.resolve_icall(
        "UnityEngine.Application::set_targetFrameRate(System.Int32)"));
    g_managed.get_fps = reinterpret_cast<GetTargetFps>(g_managed.resolve_icall(
        "UnityEngine.Application::get_targetFrameRate()"));
    Log("Unity icalls: set_targetFrameRate=%s get_targetFrameRate=%s",
        g_managed.set_fps ? "found" : "MISSING",
        g_managed.get_fps ? "found" : "MISSING");
  }
}

// Called on the game's main thread. Looks for
// Beyond.DeviceInfo.{ChangeInputType(1 arg), get_inputType(0 args)}; true when
// ChangeInputType is there.
bool ResolveMethods() {
  if (g_managed.change_input_type)
    return true;
  HMODULE game = g_managed.game_assembly;
  if (!game)
    return false;
  auto domain_get =
      reinterpret_cast<DomainGet>(GetProcAddress(game, "il2cpp_domain_get"));
  auto assembly_open = reinterpret_cast<AssemblyOpen>(
      GetProcAddress(game, "il2cpp_domain_assembly_open"));
  auto assembly_image = reinterpret_cast<AssemblyImage>(
      GetProcAddress(game, "il2cpp_assembly_get_image"));
  auto class_from_name = reinterpret_cast<ClassFromName>(
      GetProcAddress(game, "il2cpp_class_from_name"));
  auto method_from_name = reinterpret_cast<MethodFromName>(
      GetProcAddress(game, "il2cpp_class_get_method_from_name"));
  g_managed.runtime_invoke = reinterpret_cast<RuntimeInvoke>(
      GetProcAddress(game, "il2cpp_runtime_invoke"));
  g_managed.object_unbox = reinterpret_cast<ObjectUnbox>(
      GetProcAddress(game, "il2cpp_object_unbox"));
  if (!g_managed.exports_logged) {
    g_managed.exports_logged = true;
    Log("GameAssembly exports: domain_get=%d assembly_open=%d "
        "assembly_get_image=%d class_from_name=%d "
        "class_get_method_from_name=%d runtime_invoke=%d object_unbox=%d",
        domain_get != nullptr, assembly_open != nullptr,
        assembly_image != nullptr, class_from_name != nullptr,
        method_from_name != nullptr, g_managed.runtime_invoke != nullptr,
        g_managed.object_unbox != nullptr);
  }
  if (!domain_get || !assembly_open || !assembly_image || !class_from_name ||
      !method_from_name || !g_managed.runtime_invoke)
    return false;
  void *domain = domain_get();
  if (!domain)
    return false;
  const char *assemblies[] = {"Common.Beyond", "Assembly-CSharp"};
  for (const char *name : assemblies) {
    const void *assembly = assembly_open(domain, name);
    const void *image = assembly ? assembly_image(assembly) : nullptr;
    void *klass =
        image ? class_from_name(image, "Beyond", "DeviceInfo") : nullptr;
    if (!klass)
      continue;
    g_managed.change_input_type = method_from_name(klass, "ChangeInputType", 1);
    g_managed.get_input_type = method_from_name(klass, "get_inputType", 0);
    Log("Beyond.DeviceInfo found in %s: ChangeInputType=%s get_inputType=%s",
        name, g_managed.change_input_type ? "found" : "MISSING",
        g_managed.get_input_type ? "found" : "MISSING");
    if (g_managed.change_input_type)
      return true;
  }
  // the class under any other assembly name: walk every assembly the domain
  // holds
  using DomainAssemblies = const void **(__cdecl *)(void *, size_t *);
  auto domain_assemblies = reinterpret_cast<DomainAssemblies>(
      GetProcAddress(game, "il2cpp_domain_get_assemblies"));
  if (domain_assemblies) {
    size_t count = 0;
    const void **list = domain_assemblies(domain, &count);
    for (size_t i = 0; list && i < count && i < 4096; ++i) {
      const void *image = list[i] ? assembly_image(list[i]) : nullptr;
      void *klass =
          image ? class_from_name(image, "Beyond", "DeviceInfo") : nullptr;
      if (!klass)
        continue;
      g_managed.change_input_type =
          method_from_name(klass, "ChangeInputType", 1);
      g_managed.get_input_type = method_from_name(klass, "get_inputType", 0);
      Log("Beyond.DeviceInfo found by walking %zu assemblies (#%zu): "
          "ChangeInputType=%s get_inputType=%s",
          count, i, g_managed.change_input_type ? "found" : "MISSING",
          g_managed.get_input_type ? "found" : "MISSING");
      if (g_managed.change_input_type)
        return true;
    }
  }
  if (!g_managed.methods_logged) {
    g_managed.methods_logged = true;
    Log("Beyond.DeviceInfo.ChangeInputType is not there yet (the game has not "
        "loaded its managed assemblies); trying again");
  }
  return false;
}

// -1 when it cannot be read
int ReadInputType() {
  if (!g_managed.get_input_type || !g_managed.runtime_invoke)
    return -1;
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(g_managed.get_input_type, nullptr,
                                         nullptr, &exception);
  if (exception || !boxed)
    return -1;
  const void *value =
      g_managed.object_unbox
          ? g_managed.object_unbox(boxed)
          : static_cast<const char *>(boxed) + 2 * sizeof(void *);
  return value ? *static_cast<const int32_t *>(value) : -1;
}

bool CallChangeInputType(int32_t type) {
  void *args[] = {&type};
  void *exception = nullptr;
  g_managed.runtime_invoke(g_managed.change_input_type, nullptr, args,
                           &exception);
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
  // every change of what the game says is logged, and the first applications; a
  // game that flips back again and again does not fill the log
  if (applies <= 10 || reading != g_last_reading)
    Log("input type was %d, ChangeInputType(Touch) %s, now %d (application "
        "%ld)",
        reading, ok ? "returned" : "RAISED an exception", after, applies);
  g_last_reading = reading;
  if (!ok && !g_touch_notice) {
    g_touch_notice = true;
    Log("the managed call raised: the game may not be ready for it; trying "
        "again next tick");
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

// ---- EXPERIMENT exp-drag: Unity UI cancels a click as a drag when the finger
// moves more than EventSystem.pixelDragThreshold between down and up. On a 2868
// x 1320 stream a few pixels (the desktop value) are exceeded by ordinary
// finger jitter: the press shows (sound, effect) but the click never fires.
// Natively the threshold is DPI-scaled. The only write:
// EventSystem.pixelDragThreshold, once per EventSystem object (and again only
// if the game resets it). drag_px in endfield-ui.ini (or env ENDFIELD_DRAG_PX):
// auto | <pixels> | off. The game's own thresholds
// (Beyond.UI.UIConst.dragThreshold, UIDragHandler / UITouchPanel / UIButton
// fields) are only READ and logged.
using DomainAssembliesFn = const void **(__cdecl *)(void *, size_t *);
using ClassGetMethodsFn = const void *(__cdecl *)(void *, void **);
using MethodNameFn = const char *(__cdecl *)(const void *);
using ParamCountFn = unsigned(__cdecl *)(const void *);
using IsGenericFn = bool(__cdecl *)(const void *);
using FieldFromNameFn = void *(__cdecl *)(void *, const char *);
using FieldGetFn = void(__cdecl *)(void *, void *, void *);
using FieldTypeFn = const void *(__cdecl *)(void *);
using TypeKindFn = int(__cdecl *)(const void *);
using RetTypeFn = const void *(__cdecl *)(const void *);
using StaticGetFn = void(__cdecl *)(void *, void *);
using ClassTypeFn = const void *(__cdecl *)(void *);
using TypeObjectFn = void *(__cdecl *)(const void *);
using ArrayLengthFn = size_t(__cdecl *)(void *);
using IntIcall = int(__cdecl *)();

struct DragApi {
  bool resolved = false, failed_logged = false;
  void *es_class = nullptr;
  const void *get_th = nullptr, *set_th = nullptr, *get_current = nullptr,
             *find_objects = nullptr;
  int find_args = 0;
  FieldFromNameFn field_by_name = nullptr;
  FieldGetFn field_get = nullptr;
  FieldTypeFn field_type = nullptr;
  TypeKindFn type_kind = nullptr;
  RetTypeFn ret_type = nullptr;
  StaticGetFn static_get = nullptr;
  ClassTypeFn class_type = nullptr;
  TypeObjectFn type_object = nullptr;
  ArrayLengthFn array_length = nullptr;
  IntIcall screen_w = nullptr, screen_h = nullptr;
};
DragApi g_drag;
std::string g_drag_ini_path;
std::string g_drag_setting = "auto"; // auto | off | <pixels>
int g_drag_target = 0;
int g_drag_last_logged_target = -1;
std::unordered_map<void *, int>
    g_es_seen; // EventSystem object -> value we last set
LONG g_es_resets = 0, g_drag_pumps = 0, g_survey_stage = 0;
bool g_drag_scan_full =
    false; // drag_scan=full: the last build's FindObjectsOfType over all
           // EventSystems every second (heavy on the game thread); default:
           // EventSystem.current, full scan every 30 s

void *FindClassIn(const char *ns, const char *name) {
  HMODULE game = g_managed.game_assembly;
  auto domain_get =
      reinterpret_cast<DomainGet>(GetProcAddress(game, "il2cpp_domain_get"));
  auto assembly_image = reinterpret_cast<AssemblyImage>(
      GetProcAddress(game, "il2cpp_assembly_get_image"));
  auto class_from_name = reinterpret_cast<ClassFromName>(
      GetProcAddress(game, "il2cpp_class_from_name"));
  auto domain_assemblies = reinterpret_cast<DomainAssembliesFn>(
      GetProcAddress(game, "il2cpp_domain_get_assemblies"));
  if (!domain_get || !assembly_image || !class_from_name || !domain_assemblies)
    return nullptr;
  size_t count = 0;
  const void **list = domain_assemblies(domain_get(), &count);
  for (size_t i = 0; list && i < count && i < 4096; ++i) {
    const void *image = list[i] ? assembly_image(list[i]) : nullptr;
    void *klass = image ? class_from_name(image, ns, name) : nullptr;
    if (klass)
      return klass;
  }
  return nullptr;
}

bool ResolveDrag() {
  if (g_drag.resolved)
    return true;
  FindModules();
  HMODULE game = g_managed.game_assembly;
  if (!game || !g_managed.runtime_invoke)
    return false;
  auto method_from_name = reinterpret_cast<MethodFromName>(
      GetProcAddress(game, "il2cpp_class_get_method_from_name"));
  auto get_methods = reinterpret_cast<ClassGetMethodsFn>(
      GetProcAddress(game, "il2cpp_class_get_methods"));
  auto method_name = reinterpret_cast<MethodNameFn>(
      GetProcAddress(game, "il2cpp_method_get_name"));
  auto param_count = reinterpret_cast<ParamCountFn>(
      GetProcAddress(game, "il2cpp_method_get_param_count"));
  auto is_generic = reinterpret_cast<IsGenericFn>(
      GetProcAddress(game, "il2cpp_method_is_generic"));
  g_drag.field_by_name = reinterpret_cast<FieldFromNameFn>(
      GetProcAddress(game, "il2cpp_class_get_field_from_name"));
  g_drag.field_get = reinterpret_cast<FieldGetFn>(
      GetProcAddress(game, "il2cpp_field_get_value"));
  g_drag.field_type = reinterpret_cast<FieldTypeFn>(
      GetProcAddress(game, "il2cpp_field_get_type"));
  g_drag.type_kind = reinterpret_cast<TypeKindFn>(
      GetProcAddress(game, "il2cpp_type_get_type"));
  g_drag.ret_type = reinterpret_cast<RetTypeFn>(
      GetProcAddress(game, "il2cpp_method_get_return_type"));
  g_drag.static_get = reinterpret_cast<StaticGetFn>(
      GetProcAddress(game, "il2cpp_field_static_get_value"));
  g_drag.class_type = reinterpret_cast<ClassTypeFn>(
      GetProcAddress(game, "il2cpp_class_get_type"));
  g_drag.type_object = reinterpret_cast<TypeObjectFn>(
      GetProcAddress(game, "il2cpp_type_get_object"));
  g_drag.array_length = reinterpret_cast<ArrayLengthFn>(
      GetProcAddress(game, "il2cpp_array_length"));
  if (!method_from_name || !get_methods || !method_name || !param_count ||
      !is_generic || !g_drag.field_by_name || !g_drag.field_get ||
      !g_drag.field_type || !g_drag.type_kind || !g_drag.ret_type ||
      !g_drag.static_get || !g_drag.class_type || !g_drag.type_object ||
      !g_drag.array_length) {
    if (!g_drag.failed_logged) {
      g_drag.failed_logged = true;
      Log("drag: needed il2cpp exports are missing; nothing is changed");
    }
    return false;
  }
  g_drag.es_class = FindClassIn("UnityEngine.EventSystems", "EventSystem");
  void *object_class = FindClassIn("UnityEngine", "Object");
  if (!g_drag.es_class || !object_class)
    return false; // not loaded yet
  for (int want : {2, 1}) {
    void *iter = nullptr;
    while (const void *m = get_methods(object_class, &iter)) {
      const char *n = method_name(m);
      if (n && strcmp(n, "FindObjectsOfType") == 0 && !is_generic(m) &&
          static_cast<int>(param_count(m)) == want) {
        g_drag.find_objects = m;
        g_drag.find_args = want;
        break;
      }
    }
    if (g_drag.find_objects)
      break;
  }
  g_drag.get_th =
      method_from_name(g_drag.es_class, "get_pixelDragThreshold", 0);
  g_drag.set_th =
      method_from_name(g_drag.es_class, "set_pixelDragThreshold", 1);
  g_drag.get_current = method_from_name(g_drag.es_class, "get_current", 0);
  Log("drag: resolved EventSystem=%p get/set pixelDragThreshold=%d/%d "
      "get_current=%d FindObjectsOfType(%d args)=%s",
      g_drag.es_class, g_drag.get_th != nullptr, g_drag.set_th != nullptr,
      g_drag.get_current != nullptr, g_drag.find_args,
      g_drag.find_objects ? "found" : "MISSING");
  if (!g_drag.get_th || !g_drag.set_th || !g_drag.find_objects) {
    if (!g_drag.failed_logged) {
      g_drag.failed_logged = true;
      Log("drag: cannot set the threshold on this build (see the line above); "
          "nothing is changed");
    }
    return false;
  }
  g_drag.resolved = true;
  return true;
}

size_t DragFindObjects(void *klass, void ***out) {
  *out = nullptr;
  const void *type = g_drag.class_type(klass);
  void *type_object = type ? g_drag.type_object(type) : nullptr;
  if (!type_object)
    return 0;
  bool include_inactive = true;
  void *args[2] = {type_object, &include_inactive};
  void *exception = nullptr;
  void *array =
      g_managed.runtime_invoke(g_drag.find_objects, nullptr, args, &exception);
  if (exception || !array)
    return 0;
  const size_t length = g_drag.array_length(array);
  if (length > 100000)
    return 0;
  *out = reinterpret_cast<void **>(static_cast<char *>(array) +
                                   0x20); // Il2CppArray: header 16, bounds 8,
                                          // max_length 8, then the elements
  return length;
}

bool DragReadInt(const void *getter, void *object, int *out) {
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(getter, object, nullptr, &exception);
  if (exception || !boxed)
    return false;
  const void *raw = g_managed.object_unbox
                        ? g_managed.object_unbox(boxed)
                        : static_cast<const char *>(boxed) + 2 * sizeof(void *);
  if (!raw)
    return false;
  *out = *static_cast<const int *>(raw);
  return true;
}

// a number out of a getter / field of any numeric type, as text for the log
bool NumberAsDouble(int kind, const unsigned char *raw, double *out) {
  switch (kind) {
  case 2:
  case 4:
  case 5:
    *out = raw[0];
    return true;
  case 6:
    *out = *reinterpret_cast<const int16_t *>(raw);
    return true;
  case 7:
    *out = *reinterpret_cast<const uint16_t *>(raw);
    return true;
  case 8:
    *out = *reinterpret_cast<const int32_t *>(raw);
    return true;
  case 9:
    *out = *reinterpret_cast<const uint32_t *>(raw);
    return true;
  case 10:
  case 11:
    *out = static_cast<double>(*reinterpret_cast<const int64_t *>(raw));
    return true;
  case 12:
    *out = *reinterpret_cast<const float *>(raw);
    return true;
  case 13:
    *out = *reinterpret_cast<const double *>(raw);
    return true;
  }
  return false;
}
bool StaticGetter(void *klass, const char *name, double *out) {
  auto method_from_name = reinterpret_cast<MethodFromName>(GetProcAddress(
      g_managed.game_assembly, "il2cpp_class_get_method_from_name"));
  const void *m = method_from_name(klass, name, 0);
  if (!m)
    return false;
  const int kind = g_drag.type_kind(g_drag.ret_type(m));
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(m, nullptr, nullptr, &exception);
  if (exception || !boxed)
    return false;
  const void *raw = g_managed.object_unbox
                        ? g_managed.object_unbox(boxed)
                        : static_cast<const char *>(boxed) + 2 * sizeof(void *);
  return raw &&
         NumberAsDouble(kind, static_cast<const unsigned char *>(raw), out);
}
bool StaticConst(void *klass, const char *name, double *out) {
  void *field = g_drag.field_by_name(klass, name);
  if (!field)
    return false;
  unsigned char raw[16] = {};
  g_drag.static_get(field, raw);
  return NumberAsDouble(g_drag.type_kind(g_drag.field_type(field)), raw, out);
}
// 0.1 inch of the client device in stream pixels: ppi by stream size (landscape
// W x H)
struct PpiRow {
  int w, h, ppi;
};
const PpiRow kPpi[] = {
    {2868, 1320, 460}, {2622, 1206, 460}, {2736, 1260, 460}, {2556, 1179, 460},
    {2796, 1290, 460}, {2532, 1170, 460}, {2778, 1284, 458}, {2340, 1080, 476},
    {2436, 1125, 458}, {2688, 1242, 458}, {1792, 828, 326},  {1334, 750, 326},
    {2266, 1488, 326}, {2360, 1640, 264}, {2388, 1668, 264}, {2420, 1668, 264},
    {2732, 2048, 264}, {2752, 2064, 264}, {2160, 1620, 264}};

void ReadDragSetting() {
  g_drag_setting = "auto";
  char env[64] = {};
  if (GetEnvironmentVariableA("ENDFIELD_DRAG_PX", env, sizeof(env)) > 0)
    g_drag_setting = env;
  if (g_drag_ini_path.empty() && g_module) {
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(g_module, path, MAX_PATH);
    std::wstring dir = path;
    const size_t slash = dir.find_last_of(L"\\/");
    dir = (slash == std::wstring::npos ? std::wstring()
                                       : dir.substr(0, slash + 1)) +
          L"endfield-ui.ini";
    char narrow[MAX_PATH * 3] = {};
    WideCharToMultiByte(CP_ACP, 0, dir.c_str(), -1, narrow, sizeof(narrow) - 1,
                        nullptr, nullptr);
    g_drag_ini_path = narrow;
  }
  FILE *f =
      g_drag_ini_path.empty() ? nullptr : fopen(g_drag_ini_path.c_str(), "rb");
  bool seen_pointers = false;
  if (f) {
    char line[256];
    while (fgets(line, sizeof(line), f)) {
      if (_strnicmp(line, "touch_mouse=", 12) == 0) {
        const LONG want = _strnicmp(line + 12, "off", 3) == 0 ? 0 : 1;
        if (want != g_drop_touch_mouse)
          Log("touch_mouse: the ini says %s",
              want ? "drop (the mouse messages made from touches are removed "
                     "before the game reads them)"
                   : "off (nothing is removed)");
        g_drop_touch_mouse = want;
      }
      if (_strnicmp(line, "ui_pointers=", 12) == 0) {
        const char *v = line + 12;
        const LONG want =
            _strnicmp(v, "off", 3) == 0
                ? -1
                : (_strnicmp(v, "all", 3) == 0
                       ? 2
                       : ((v[0] >= 0x30 && v[0] <= 0x32) ? v[0] - 0x30 : 2));
        if (want != g_pm_want)
          Log("ui_pointers: the ini says %ld (-1 off, 0 "
              "SingleMouseOrPenButMultiTouchAndTrack, 1 SingleUnifiedPointer, "
              "2 AllPointersAsIs)",
              want);
        g_pm_want = want;
        seen_pointers = true;
      }
      if (_strnicmp(line, "drag_scan=", 10) == 0)
        g_drag_scan_full = _strnicmp(line + 10, "full", 4) == 0;
      if (_strnicmp(line, "drag_px=", 8) == 0) {
        g_drag_setting = line + 8;
        while (!g_drag_setting.empty() &&
               (g_drag_setting.back() == '\r' ||
                g_drag_setting.back() == '\n' || g_drag_setting.back() == ' '))
          g_drag_setting.pop_back();
      }
    }
    fclose(f);
  }
  if (!seen_pointers)
    g_pm_want = 2;
}

// the value to set, for the current stream size; 0 = do not set
int ComputeDragTarget(int original, std::string *basis) {
  if (_stricmp(g_drag_setting.c_str(), "off") == 0) {
    *basis = "drag_px=off";
    return 0;
  }
  char *end = nullptr;
  const double manual = strtod(g_drag_setting.c_str(), &end);
  if (end != g_drag_setting.c_str() && manual >= 1 && manual <= 400) {
    *basis = "drag_px from the ini";
    return static_cast<int>(manual + 0.5);
  }
  const int w = g_drag.screen_w ? g_drag.screen_w() : 0,
            h = g_drag.screen_h ? g_drag.screen_h() : 0;
  const int W = w > h ? w : h, H = w > h ? h : w;
  double px = 0;
  char text[160];
  for (const PpiRow &r : kPpi)
    if (r.w == W && r.h == H) {
      px = 0.1 * r.ppi;
      snprintf(text, sizeof(text),
               "auto: 0.1 inch at %d ppi (table, stream %d x %d)", r.ppi, W, H);
      *basis = text;
      break;
    }
  if (px == 0) {
    px = 0.015 * H;
    snprintf(
        text, sizeof(text),
        "auto: 1.5%% of the stream height %d (device not in the ppi table)", H);
    *basis = text;
  }
  if (px < 8)
    px = 8;
  if (px > 100)
    px = 100;
  int target = static_cast<int>(px + 0.5);
  if (target < original) {
    *basis += "; the game's own value is already larger, kept";
    target = original;
  }
  return target;
}

void EnsureDrag() {
  const LONG n = InterlockedIncrement(&g_drag_pumps);
  if (n != 1 && n % 4 != 1)
    return; // once a second: find the EventSystem(s) and look at the one field
  if (!ResolveDrag())
    return;
  if (n == 1 || n % 20 == 1)
    ReadDragSetting();
  ResolveUnity();
  if (g_managed.resolve_icall && !g_drag.screen_w) {
    g_drag.screen_w = reinterpret_cast<IntIcall>(
        g_managed.resolve_icall("UnityEngine.Screen::get_width()"));
    g_drag.screen_h = reinterpret_cast<IntIcall>(
        g_managed.resolve_icall("UnityEngine.Screen::get_height()"));
  }
  void *current = nullptr;
  if (g_drag.get_current) {
    void *exception = nullptr;
    current = g_managed.runtime_invoke(g_drag.get_current, nullptr, nullptr,
                                       &exception);
    if (exception)
      current = nullptr;
  }
  // FindObjectsOfType(EventSystem, includeInactive) walks every MonoBehaviour
  // in the game: with a big scene that is a hitch every second.
  // EventSystem.current is one static read.
  const bool full_scan =
      g_drag_scan_full || n == 1 || n % 120 == 1 || (!current && n % 16 == 1);
  void **systems = nullptr;
  void *single[1] = {current};
  size_t count = 0;
  if (full_scan) {
    count = DragFindObjects(g_drag.es_class, &systems);
  } else {
    systems = single;
    count = 1;
  }
  if (count == 0)
    return;
  std::unordered_map<void *, int> now;
  for (size_t i = 0; i < count; ++i) {
    void *es = systems[i];
    if (!es)
      continue;
    int value = 0;
    if (!DragReadInt(g_drag.get_th, es, &value))
      continue;
    auto it = g_es_seen.find(es);
    const bool known = it != g_es_seen.end();
    std::string basis;
    const int target = ComputeDragTarget(known ? it->second : value, &basis);
    if (!known) {
      Log("drag: EventSystem %p%s: pixelDragThreshold ORIGINAL = %d (metadata "
          "field m_DragThreshold; Unity's default is 10)",
          es, es == current ? " (the active one)" : "", value);
    }
    if (target <= 0) {
      now[es] = value;
      if (!known)
        Log("drag: %s: not set", basis.c_str());
      continue;
    }
    if (target != g_drag_last_logged_target) {
      Log("drag: target %d px (%s)", target, basis.c_str());
      g_drag_last_logged_target = target;
    }
    int written = value;
    if (value != target) {
      int v = target;
      void *args[] = {&v};
      void *exception = nullptr;
      g_managed.runtime_invoke(g_drag.set_th, es, args, &exception);
      DragReadInt(g_drag.get_th, es, &written);
      if (!known)
        Log("drag: EventSystem %p: pixelDragThreshold %d -> %d (read back "
            "%d)%s",
            es, value, target, written,
            exception ? " [the setter raised an exception]" : "");
      else if (InterlockedIncrement(&g_es_resets) <= 40) {
        if (value != it->second)
          Log("drag: the GAME changed pixelDragThreshold on EventSystem %p to "
              "%d (we had set %d); set again to %d (read back %d)",
              es, value, it->second, target, written);
        else
          Log("drag: target changed (ini or stream size): EventSystem %p %d -> "
              "%d (read back %d)",
              es, value, target, written);
      }
    }
    now[es] = written;
  }
  if (full_scan)
    g_es_seen.swap(now);
  else
    for (auto &entry : now)
      g_es_seen[entry.first] = entry.second;
}
#include "endfield_pointer_mode.inc"

// ---- EXPERIMENT (endfield-touch-ui-scale-exp): make the touch UI bigger ----
// Finding (static, global-metadata.dat): the game's own UI scaling is
// Beyond.UI.UICanvasScaleHelper driving UnityEngine.UI.CanvasScaler
// (m_UiScaleMode / m_ReferenceResolution / m_ScaleFactor).
// UnityEngine.Screen.dpi is read only by third-party code (UIElements
// PanelSettings, MagicaCloth, SRF), so the Genshin-style DPI hook would change
// nothing here. Lever: every CanvasScaler: ScaleWithScreenSize ->
// referenceResolution / factor (a smaller reference resolution = bigger UI);
// ConstantPixelSize -> scaleFactor * factor. The factor comes from env
// ENDFIELD_UI_SCALE or endfield-ui.ini (`scale=2.0`) next to this DLL; 1.0 =
// off. The ini is re-read about every 5 s, so it can be changed while the game
// runs. Optional `mode=helper` in the ini: instead of the scalers, set the
// helper's own custom standard resolution
// (_useCustomStandardResolution/_customStandardResolution) to standard/factor.
struct Vec2 {
  float x, y;
};
using ClassGetType = const void *(__cdecl *)(void *);
using TypeGetObject = void *(__cdecl *)(const void *);
using ArrayLength = size_t(__cdecl *)(void *);
using ClassGetMethods = const void *(__cdecl *)(void *, void **);
using MethodGetName = const char *(__cdecl *)(const void *);
using MethodGetParamCount = unsigned(__cdecl *)(const void *);
using MethodIsGeneric = bool(__cdecl *)(const void *);
using ClassGetField = void *(__cdecl *)(void *, const char *);
using FieldGetValue = void(__cdecl *)(void *, void *, void *);
using FieldSetValue = void(__cdecl *)(void *, void *, void *);
using DomainAssemblies2 = const void **(__cdecl *)(void *, size_t *);
using MethodGetReturnType = const void *(__cdecl *)(const void *);
using TypeGetType = int(__cdecl *)(const void *);

struct UiLever {
  bool resolved = false;
  bool failed_logged = false;
  void *scaler_class = nullptr;
  void *helper_class = nullptr;
  const void *find_objects = nullptr;
  int find_args = 0;
  const void *m_get_ref = nullptr, *m_set_ref = nullptr, *m_get_mode = nullptr,
             *m_get_sf = nullptr, *m_set_sf = nullptr;
  const void *m_h_std_x = nullptr, *m_h_std_y = nullptr, *m_h_force = nullptr;
  void *f_h_use = nullptr, *f_h_res = nullptr, *f_h_pc = nullptr;
  const void *m_h_scale = nullptr;
  TypeGetObject type_get_object = nullptr;
  ClassGetType class_get_type = nullptr;
  ArrayLength array_length = nullptr;
  FieldGetValue field_get = nullptr;
  FieldSetValue field_set = nullptr;
  MethodGetReturnType ret_type = nullptr;
  TypeGetType type_type = nullptr;
  void *canvas_class = nullptr;
  const void *m_c_root = nullptr, *m_c_mode = nullptr, *m_c_get_sf = nullptr,
             *m_c_set_sf = nullptr, *m_name = nullptr;
  const void *m_get_fallback_dpi = nullptr, *m_get_physical_unit = nullptr,
             *m_set_mode = nullptr;
  void *f_scaler_canvas = nullptr;
  float (*get_dpi)() = nullptr;
};
UiLever g_ui;
struct ScalerState {
  Vec2 orig_ref, written_ref;
  float orig_sf, written_sf;
  bool applied;
  bool was_physical;
};
struct CanvasState {
  float orig_sf, written_sf;
  bool applied;
};
std::unordered_map<void *, CanvasState> g_canvases;
LONG g_ui_fast_until = 0;
LONG g_ui_log_budget = 260;
std::unordered_map<void *, ScalerState> g_scalers;
float g_ui_factor = 1.0f;
bool g_ui_helper_mode = false;
float g_ui_logged_factor = -1.0f;
bool g_flag_legacy =
    false; // legacy_scaler=1: the old periodic CanvasScaler rewrite (scale=)
bool g_flag_canvases = false; // canvases=1 (with legacy_scaler=1): scale
                              // scaler-less root canvases too
bool g_flag_safe_override =
    false; // safearea_override=1: replace the native Screen.safeArea function
bool g_flag_observe =
    false; // observe=0: no read-only logging of new canvases / scalers
bool g_flag_notch_write =
    false; // notch_write=0: never write the game's notch-padding setting
int g_ui_knob_mode = 0; // ui_knob: 0 auto (pc scale, then standard resolution),
                        // 1 pc, 2 std, 3 off
float g_ui_rel = 0.0f;  // ui_scale=<f>: UI enlargement relative to the PC
                        // default; 0 = auto (match the phone build)
volatile LONG g_cloud_mode = 0; // cloud_mode=on: the user-platform emulation of
                                // the game's cloud design (endfield_cloud.inc)
bool g_cloud_active = false;    // the two hot-fix slots are filled
LONG g_cloud_since = 0;
bool g_cloud_fallback =
    false; // cloud_mode=on but the slots could not be filled in time: the PC
           // path (size knob, notch setting) for the session
int g_ini_knob_mode =
    0; // ui_knob and notch_write as the ini asks, kept for the fallback
bool g_ini_notch_write = true;
float g_cloud_pc_scale = 1.25f; // UIConst.PC_REFERENCE_RESOLUTION_SCALE
bool g_ui_off = false; // ui_scale=off: the game's own value (applied live: the
                       // original is put back)
bool g_force_profile =
    false; // the ini changed: the device profile is rebuilt on the next pump
bool g_notch_reeval =
    false; // safe_area changed: the notch value is worked out again
std::string g_safe_cfg_key;
bool g_ui_auto =
    true; // no scale= in the ini: the game's own mobile logic decides
float g_ui_manual = 1.0f; // scale= from the ini or ENDFIELD_UI_SCALE
LONG g_ui_pumps = 0;
LONG g_ui_scans = 0;
LONG g_ui_writes = 0;
std::wstring g_ini_path;

// ---- device profile: what the CLIENT device is, so the game's own mobile
// logic can behave as it does natively ---- Sources, in priority order: (a)
// endfield-ui.ini, (b) the host session (ENDFIELD_DEVICE env, or
// endfield-device.ini next to the DLL; same keys), (c) the built-in table keyed
// by stream resolution / aspect, (d) a fallback by aspect.
struct Dev {
  const char *key, *name, *cls;
  int w, h;
  float width_pt, inset_pt;
  float ui_scale = 0;
}; // ui_scale: the UI size for this device when the ini says auto (0 = the
   // game's PC multiplier removed)
// landscape pixels; width_pt = landscape width in points; inset_pt = the
// landscape side inset (left = right) of the safe area in points. The game
// insets sides only (top and bottom 0).
const Dev kDevices[] = {
    {"iphone-se", "iPhone SE (2/3), 8", "phone", 1334, 750, 667, 0},
    {"iphone-x", "iPhone X / XS / 11 Pro", "phone", 2436, 1125, 812, 44},
    {"iphone-xr", "iPhone XR / 11", "phone", 1792, 828, 896, 44},
    {"iphone-xs-max", "iPhone XS Max / 11 Pro Max", "phone", 2688, 1242, 896,
     44},
    {"iphone-12-mini", "iPhone 12 mini / 13 mini", "phone", 2340, 1080, 780,
     50},
    {"iphone-12", "iPhone 12 / 12 Pro / 13 / 13 Pro / 14 / 16e", "phone", 2532,
     1170, 844, 47},
    {"iphone-12-pro-max", "iPhone 12 Pro Max / 13 Pro Max / 14 Plus", "phone",
     2778, 1284, 926, 47},
    {"iphone-14-pro", "iPhone 14 Pro / 15 / 15 Pro / 16", "phone", 2556, 1179,
     852, 59},
    {"iphone-14-pro-max", "iPhone 14 Pro Max / 15 Plus / 15 Pro Max / 16 Plus",
     "phone", 2796, 1290, 932, 59},
    {"iphone-16-pro", "iPhone 16 Pro / 17 / 17 Pro", "phone", 2622, 1206, 874,
     62},
    {"iphone-air", "iPhone Air", "phone", 2736, 1260, 912, 62},
    {"iphone-17-pro-max", "iPhone 17 Pro Max / 16 Pro Max", "phone", 2868, 1320,
     956, 62, 1.40f},
    {"ipad-9", "iPad 9th gen", "pad", 2160, 1620, 1080, 0},
    {"ipad-mini", "iPad mini 6 / 7", "pad", 2266, 1488, 1133, 0},
    {"ipad-air-11", "iPad Air 11 / iPad 10", "pad", 2360, 1640, 1180, 0},
    {"ipad-pro-11", "iPad Pro 11 (M1/M2)", "pad", 2388, 1668, 1194, 0},
    {"ipad-pro-11-m4", "iPad Pro 11 (M4/M5)", "pad", 2420, 1668, 1210, 0},
    {"ipad-air-13", "iPad Air 13 / iPad Pro 12.9", "pad", 2732, 2048, 1366, 0},
    {"ipad-pro-13", "iPad Pro 13 (M4/M5)", "pad", 2752, 2064, 1376, 0},
    {"android-phone-1080p", "Android phone 2400x1080 (e.g. Pixel 8a)", "phone",
     2400, 1080, 0, 0},
    {"android-phone-2412", "Android phone 2412x1080", "phone", 2412, 1080, 0,
     0},
    {"android-phone-3120", "Android phone 3120x1440 (Galaxy S Ultra)", "phone",
     3120, 1440, 0, 0},
    {"android-phone-3200", "Android phone 3200x1440", "phone", 3200, 1440, 0,
     0},
    {"android-phone-2992", "Pixel 9 Pro XL class 2992x1344", "phone", 2992,
     1344, 0, 0},
    {"android-tablet-2560",
     "Android tablet 2560x1600 (Galaxy Tab S9 / Pixel Tablet)", "pad", 2560,
     1600, 0, 0},
    {"android-tablet-2960", "Galaxy Tab S9 Ultra 2960x1848", "pad", 2960, 1848,
     0, 0},
    {"android-tablet-2000", "Android tablet 2000x1200", "pad", 2000, 1200, 0,
     0},
};
struct ProfileSpec {
  std::string device, cls;
  float width_pt = 0, l_pt = -1, r_pt = -1, t_pt = -1, b_pt = -1;
  float l_px = 0, r_px = 0, t_px = 0, b_px = 0;
  bool Any() const {
    return !device.empty() || !cls.empty() || width_pt > 0 || l_pt >= 0 ||
           r_pt >= 0 || t_pt >= 0 || b_pt >= 0 || l_px > 0 || r_px > 0 ||
           t_px > 0 || b_px > 0;
  }
};
struct Profile {
  std::string source = "none", name = "none", cls = "";
  float width_pt = 0, l_pt = 0, r_pt = 0, t_pt = 0, b_pt = 0, l_px = 0,
        r_px = 0, t_px = 0, b_px = 0, ui_scale = 0;
  bool use_px = false;
  bool Same(const Profile &o) const {
    return source == o.source && ui_scale == o.ui_scale && name == o.name &&
           cls == o.cls && width_pt == o.width_pt && l_pt == o.l_pt &&
           r_pt == o.r_pt && t_pt == o.t_pt && b_pt == o.b_pt &&
           l_px == o.l_px && r_px == o.r_px && t_px == o.t_px &&
           b_px == o.b_px && use_px == o.use_px;
  }
};
ProfileSpec g_spec_ini, g_spec_host;
Profile g_profile;
bool g_have_profile = false;

void ParseSpecLine(const char *line, ProfileSpec &spec) {
  auto num = [](const char *v) { return static_cast<float>(atof(v)); };
  auto text = [](const char *v) {
    std::string t = v;
    while (!t.empty() && (t.back() == ' ' || t.back() == '\t'))
      t.pop_back();
    return t;
  };
  if (_strnicmp(line, "device=", 7) == 0)
    spec.device = text(line + 7);
  else if (_strnicmp(line, "device_class=", 13) == 0)
    spec.cls = text(line + 13);
  else if (_strnicmp(line, "width_pt=", 9) == 0)
    spec.width_pt = num(line + 9);
  else if (_strnicmp(line, "inset_left_pt=", 14) == 0)
    spec.l_pt = num(line + 14);
  else if (_strnicmp(line, "inset_right_pt=", 15) == 0)
    spec.r_pt = num(line + 15);
  else if (_strnicmp(line, "inset_top_pt=", 13) == 0)
    spec.t_pt = num(line + 13);
  else if (_strnicmp(line, "inset_bottom_pt=", 16) == 0)
    spec.b_pt = num(line + 16);
  else if (_strnicmp(line, "safe_left=", 10) == 0)
    spec.l_px = num(line + 10);
  else if (_strnicmp(line, "safe_right=", 11) == 0)
    spec.r_px = num(line + 11);
  else if (_strnicmp(line, "safe_top=", 9) == 0)
    spec.t_px = num(line + 9);
  else if (_strnicmp(line, "safe_bottom=", 12) == 0)
    spec.b_px = num(line + 12);
}

std::string ReadWholeFile(const std::wstring &path) {
  if (path.empty())
    return "";
  HANDLE file =
      CreateFileW(path.c_str(), GENERIC_READ,
                  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                  nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE)
    return "";
  char text[2048] = {};
  DWORD got = 0;
  ReadFile(file, text, sizeof(text) - 1, &got, nullptr);
  CloseHandle(file);
  return text;
}

void ReadUiConfig() {
  bool helper = false;
  bool legacy = false, canvases = false, safe_override = false, observe = true,
       notch_write = true;
  bool ui_off = false, cloud_on = true;
  int safe_mode = 0; // safe_area: 0 auto, 1 off, 2 explicit pixels
  float safe_l = 0, safe_r = 0;
  float ui_rel = 0.0f;
  int knob = 0;
  bool auto_mode = true;
  float manual = 1.0f;
  char envbuf[128] = {};
  if (GetEnvironmentVariableA("ENDFIELD_UI_SCALE", envbuf, sizeof(envbuf)) >
      0) {
    auto_mode = false;
    manual = static_cast<float>(atof(envbuf));
  }
  if (g_ini_path.empty() && g_module) {
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(g_module, path, MAX_PATH);
    std::wstring dir = path;
    const size_t slash = dir.find_last_of(L"\\/");
    g_ini_path = (slash == std::wstring::npos ? std::wstring()
                                              : dir.substr(0, slash + 1)) +
                 L"endfield-ui.ini";
  }
  ProfileSpec ini, host;
  {
    std::string text = ReadWholeFile(g_ini_path);
    for (char *line = strtok(text.data(), "\r\n"); line;
         line = strtok(nullptr, "\r\n")) {
      while (*line == ' ' || *line == '\t')
        ++line;
      if (_strnicmp(line, "scale=", 6) == 0) {
        if (_strnicmp(line + 6, "auto", 4) != 0) {
          auto_mode = false;
          manual = static_cast<float>(atof(line + 6));
        } else {
          auto_mode = true;
        }
      } else if (_strnicmp(line, "mode=", 5) == 0) {
        helper = _strnicmp(line + 5, "helper", 6) == 0;
      } else if (_strnicmp(line, "legacy_scaler=", 14) == 0) {
        legacy = atoi(line + 14) != 0;
      } else if (_strnicmp(line, "canvases=", 9) == 0) {
        canvases = atoi(line + 9) != 0;
      } else if (_strnicmp(line, "safearea_override=", 18) == 0) {
        safe_override = atoi(line + 18) != 0;
      } else if (_strnicmp(line, "observe=", 8) == 0) {
        observe = atoi(line + 8) != 0;
      } else if (_strnicmp(line, "notch_write=", 12) == 0) {
        notch_write = atoi(line + 12) != 0;
      } else if (_strnicmp(line, "cloud_mode=", 11) == 0) {
        cloud_on = _strnicmp(line + 11, "on", 2) == 0;
      } else if (_strnicmp(line, "ui_scale=", 9) == 0) {
        const char *v = line + 9;
        if (_strnicmp(v, "auto", 4) == 0) {
          ui_rel = 0.0f;
        } else if (_strnicmp(v, "off", 3) == 0) {
          ui_off = true;
        } else {
          const float f = static_cast<float>(atof(v));
          ui_rel =
              !(f > 0.0f)
                  ? 0.0f
                  : (f < 1.0f ? 1.0f
                              : (f > 1.8f ? 1.8f
                                          : f)); // the contract range 1.00
                                                 // - 1.80 is clamped; text that
                                                 // is not a number means auto
        }
      } else if (_strnicmp(line, "safe_area=", 10) == 0) {
        const char *v = line + 10;
        if (_strnicmp(v, "auto", 4) == 0) {
          safe_mode = 0;
        } else if (_strnicmp(v, "off", 3) == 0) {
          safe_mode = 1;
        } else {
          safe_mode = 2;
          safe_l = static_cast<float>(atof(v));
          const char *comma = strchr(v, 0x2c);
          safe_r = comma ? static_cast<float>(atof(comma + 1)) : safe_l;
        }
      } else if (_strnicmp(line, "ui_knob=", 8) == 0) {
        knob = _strnicmp(line + 8, "pc", 2) == 0
                   ? 1
                   : (_strnicmp(line + 8, "std", 3) == 0
                          ? 2
                          : (_strnicmp(line + 8, "off", 3) == 0 ? 3 : 0));
      } else {
        ParseSpecLine(line, ini);
      }
    }
  }
  {
    std::wstring host_path =
        g_ini_path.empty()
            ? L""
            : g_ini_path.substr(0, g_ini_path.find_last_of(L"\\/") + 1) +
                  L"endfield-device.ini";
    std::string text = ReadWholeFile(host_path);
    for (char *line = strtok(text.data(), "\r\n"); line;
         line = strtok(nullptr, "\r\n")) {
      while (*line == ' ' || *line == '\t')
        ++line;
      ParseSpecLine(line, host);
    }
    char dev[128] = {};
    if (!host.Any() &&
        GetEnvironmentVariableA("ENDFIELD_DEVICE", dev, sizeof(dev)) > 0)
      host.device = dev;
  }
  auto clampInset = [](float v) {
    return (v >= 0.0f && v <= 2000.0f) ? v : 0.0f;
  };
  if (safe_mode == 1) {
    notch_write = false;
  } else if (safe_mode == 2) {
    ini.l_px = safe_l;
    ini.r_px = safe_r;
    ini.t_px = ini.b_px = 0;
    if (!(ini.l_px > 0 || ini.r_px > 0))
      ini.cls = "phone"; // an explicit 0,0 is a profile with no insets, not
                         // "ask the table"
  }
  for (ProfileSpec *sp : {&ini, &host}) {
    sp->l_px = clampInset(sp->l_px);
    sp->r_px = clampInset(sp->r_px);
    sp->t_px = clampInset(sp->t_px);
    sp->b_px = clampInset(sp->b_px);
  }
  if (!auto_mode && !(manual >= 0.25f && manual <= 8.0f))
    manual = 1.0f;
  g_ui_auto = auto_mode;
  g_flag_legacy = legacy;
  g_flag_canvases = canvases;
  g_flag_safe_override = safe_override;
  g_flag_observe = observe;
  g_flag_notch_write = notch_write;
  g_ini_knob_mode = knob;
  g_ini_notch_write = notch_write;
  const bool cloud_path =
      cloud_on && !g_cloud_fallback; // the fallback keeps the PC path even with
                                     // cloud_mode=on
  g_ui_knob_mode =
      cloud_path
          ? 2
          : knob; // in cloud mode the game's own phone scale is the base; only
                  // the extra factor goes through the standard-resolution knob
  if (cloud_on != (g_cloud_mode != 0))
    Log("config: cloud_mode is %s",
        cloud_on ? "on (user platform = the client device, safe area from the "
                   "device profile)"
                 : "off");
  g_cloud_mode = cloud_on ? 1 : 0;
  if (cloud_path) {
    notch_write = false;
    g_flag_notch_write = false;
  }
  g_ui_rel = ui_rel;
  g_ui_off = ui_off;
  g_ui_manual = manual;
  g_ui_helper_mode = helper;
  {
    char key[96];
    snprintf(key, sizeof(key), "%d/%.1f/%.1f/%d", safe_mode, ini.l_px, ini.r_px,
             notch_write ? 1 : 0);
    if (g_safe_cfg_key != key) {
      if (!g_safe_cfg_key.empty()) {
        g_notch_reeval = true;
        g_force_profile = true;
        Log("config: safe_area changed (%s -> %s); applied once",
            g_safe_cfg_key.c_str(), key);
      }
      g_safe_cfg_key = key;
    }
  }
  g_spec_ini = ini;
  g_spec_host = host;
  const float logged = manual + (ui_off ? 50.0f : 0.0f) + safe_mode * 0.01f +
                       safe_l * 0.001f + safe_r * 0.0001f +
                       (legacy ? 100.0f : 0.0f) + ui_rel * 1000.0f +
                       knob * 10000.0f + (safe_override ? 7.0f : 0.0f) +
                       (canvases ? 3.0f : 0.0f) + (notch_write ? 0.0f : 11.0f) +
                       (observe ? 0.0f : 13.0f);
  if (g_ui_logged_factor != logged) {
    Log("config (%ls): ui_scale=%s%.3f%s ui_knob=%s legacy_scaler=%d "
        "(scale=%.3f, mode %s) canvases=%d safearea_override=%d observe=%d "
        "notch_write=%d",
        g_ini_path.c_str(), ui_rel > 0 ? "" : "auto ", ui_rel,
        ui_off ? " (off)" : "",
        knob == 1 ? "pc" : (knob == 2 ? "std" : (knob == 3 ? "off" : "auto")),
        legacy, manual, helper ? "helper" : "scaler", canvases, safe_override,
        observe, notch_write);
    g_ui_logged_factor = logged;
  }
}

const Dev *FindDeviceByKey(const std::string &key) {
  for (const Dev &d : kDevices)
    if (_stricmp(d.key, key.c_str()) == 0)
      return &d;
  return nullptr;
}

// the table row for a stream size: exact (either orientation) first, then the
// closest aspect within 1.5 %
const Dev *MatchTable(int w, int h, bool *exact) {
  const int W = w > h ? w : h, H = w > h ? h : w;
  *exact = false;
  for (const Dev &d : kDevices)
    if (d.w == W && d.h == H) {
      *exact = true;
      return &d;
    }
  const double aspect = static_cast<double>(W) / H;
  const Dev *best = nullptr;
  double best_score = 1e9;
  for (const Dev &d : kDevices) {
    const double da =
        std::fabs(static_cast<double>(d.w) / d.h - aspect) / aspect;
    if (da > 0.015 || d.w < 1600) // small panels (iPhone SE) only match
                                  // exactly: a 1920x1080 PC stream is not an SE
      continue;
    const double score =
        da * 100.0 + std::fabs(std::log(static_cast<double>(d.w) / W)) *
                         0.01; // aspect first, then closeness in size
    if (score < best_score) {
      best_score = score;
      best = &d;
    }
  }
  return best;
}

Profile BuildProfile(int w, int h) {
  Profile p;
  const ProfileSpec *spec = g_spec_ini.Any()
                                ? &g_spec_ini
                                : (g_spec_host.Any() ? &g_spec_host : nullptr);
  const Dev *dev = nullptr;
  if (spec) {
    p.source = (spec == &g_spec_ini) ? "ini" : "host";
    if (!spec->device.empty()) {
      dev = FindDeviceByKey(spec->device);
      if (!dev)
        Log("device profile: '%s' is not in the table (keys: "
            "iphone-17-pro-max, iphone-16-pro, ipad-pro-13, "
            "android-phone-1080p, ...); using the other fields",
            spec->device.c_str());
    }
  } else {
    bool exact = false;
    dev = MatchTable(w, h, &exact);
    p.source = dev ? (exact ? "table" : "table (aspect match)") : "fallback";
  }
  if (dev) {
    p.name = dev->key;
    p.cls = dev->cls;
    p.width_pt = dev->width_pt;
    p.l_pt = p.r_pt = dev->inset_pt;
    p.ui_scale = dev->ui_scale;
  } else if (!spec) {
    const int W = w > h ? w : h, H = w > h ? h : w;
    const double aspect = H > 0 ? static_cast<double>(W) / H : 0;
    p.name = "generic";
    p.cls = aspect >= 1.9 ? "phone" : (aspect <= 1.6 ? "pad" : "other");
  } else {
    p.name = "custom";
  }
  if (spec) {
    if (!spec->cls.empty())
      p.cls = spec->cls;
    if (p.cls.empty())
      p.cls = "phone";
    if (spec->width_pt > 0)
      p.width_pt = spec->width_pt;
    if (spec->l_pt >= 0)
      p.l_pt = spec->l_pt;
    if (spec->r_pt >= 0)
      p.r_pt = spec->r_pt;
    if (spec->t_pt >= 0)
      p.t_pt = spec->t_pt;
    if (spec->b_pt >= 0)
      p.b_pt = spec->b_pt;
    if (spec->l_px > 0 || spec->r_px > 0 || spec->t_px > 0 || spec->b_px > 0) {
      p.use_px = true;
      p.l_px = spec->l_px;
      p.r_px = spec->r_px;
      p.t_px = spec->t_px;
      p.b_px = spec->b_px;
    }
  }
  return p;
}

// insets in screen pixels for a screen of w x h (landscape)
void ComputeInsets(const Profile &p, int w, int h, float *l, float *r, float *t,
                   float *b) {
  if (p.use_px) {
    *l = p.l_px;
    *r = p.r_px;
    *t = p.t_px;
    *b = p.b_px;
    return;
  }
  const int W = w > h ? w : h;
  const float scale =
      p.width_pt > 0 ? static_cast<float>(W) / p.width_pt : 0.0f;
  *l = p.l_pt * scale;
  *r = p.r_pt * scale;
  *t = p.t_pt * scale;
  *b = p.b_pt * scale;
}
bool ProfileHasInsets(const Profile &p) {
  return p.use_px ? (p.l_px > 0 || p.r_px > 0 || p.t_px > 0 || p.b_px > 0)
                  : (p.width_pt > 0 &&
                     (p.l_pt > 0 || p.r_pt > 0 || p.t_pt > 0 || p.b_pt > 0));
}

void *FindClassAnywhere(const char *ns, const char *name) {
  HMODULE game = g_managed.game_assembly;
  auto domain_get =
      reinterpret_cast<DomainGet>(GetProcAddress(game, "il2cpp_domain_get"));
  auto assembly_image = reinterpret_cast<AssemblyImage>(
      GetProcAddress(game, "il2cpp_assembly_get_image"));
  auto class_from_name = reinterpret_cast<ClassFromName>(
      GetProcAddress(game, "il2cpp_class_from_name"));
  auto domain_assemblies = reinterpret_cast<DomainAssemblies2>(
      GetProcAddress(game, "il2cpp_domain_get_assemblies"));
  if (!domain_get || !assembly_image || !class_from_name || !domain_assemblies)
    return nullptr;
  size_t count = 0;
  const void **list = domain_assemblies(domain_get(), &count);
  for (size_t i = 0; list && i < count && i < 4096; ++i) {
    const void *image = list[i] ? assembly_image(list[i]) : nullptr;
    void *klass = image ? class_from_name(image, ns, name) : nullptr;
    if (klass)
      return klass;
  }
  return nullptr;
}

bool ResolveUi() {
  if (g_ui.resolved)
    return true;
  HMODULE game = g_managed.game_assembly;
  if (!game || !g_managed.runtime_invoke)
    return false;
  auto method_from_name = reinterpret_cast<MethodFromName>(
      GetProcAddress(game, "il2cpp_class_get_method_from_name"));
  auto get_methods = reinterpret_cast<ClassGetMethods>(
      GetProcAddress(game, "il2cpp_class_get_methods"));
  auto method_name = reinterpret_cast<MethodGetName>(
      GetProcAddress(game, "il2cpp_method_get_name"));
  auto param_count = reinterpret_cast<MethodGetParamCount>(
      GetProcAddress(game, "il2cpp_method_get_param_count"));
  auto is_generic = reinterpret_cast<MethodIsGeneric>(
      GetProcAddress(game, "il2cpp_method_is_generic"));
  auto get_field = reinterpret_cast<ClassGetField>(
      GetProcAddress(game, "il2cpp_class_get_field_from_name"));
  g_ui.class_get_type = reinterpret_cast<ClassGetType>(
      GetProcAddress(game, "il2cpp_class_get_type"));
  g_ui.type_get_object = reinterpret_cast<TypeGetObject>(
      GetProcAddress(game, "il2cpp_type_get_object"));
  g_ui.array_length = reinterpret_cast<ArrayLength>(
      GetProcAddress(game, "il2cpp_array_length"));
  g_ui.field_get = reinterpret_cast<FieldGetValue>(
      GetProcAddress(game, "il2cpp_field_get_value"));
  g_ui.field_set = reinterpret_cast<FieldSetValue>(
      GetProcAddress(game, "il2cpp_field_set_value"));
  g_ui.ret_type = reinterpret_cast<MethodGetReturnType>(
      GetProcAddress(game, "il2cpp_method_get_return_type"));
  g_ui.type_type = reinterpret_cast<TypeGetType>(
      GetProcAddress(game, "il2cpp_type_get_type"));
  if (!method_from_name || !get_methods || !method_name || !param_count ||
      !is_generic || !get_field || !g_ui.class_get_type ||
      !g_ui.type_get_object || !g_ui.array_length || !g_ui.field_get ||
      !g_ui.field_set) {
    if (!g_ui.failed_logged) {
      g_ui.failed_logged = true;
      Log("UI scale: needed il2cpp exports are missing (class_get_methods=%d "
          "method_get_name=%d param_count=%d is_generic=%d field_from_name=%d "
          "class_get_type=%d type_get_object=%d array_length=%d field_get=%d "
          "field_set=%d)",
          get_methods != nullptr, method_name != nullptr,
          param_count != nullptr, is_generic != nullptr, get_field != nullptr,
          g_ui.class_get_type != nullptr, g_ui.type_get_object != nullptr,
          g_ui.array_length != nullptr, g_ui.field_get != nullptr,
          g_ui.field_set != nullptr);
    }
    return false;
  }
  g_ui.scaler_class = FindClassAnywhere("UnityEngine.UI", "CanvasScaler");
  void *object_class = FindClassAnywhere("UnityEngine", "Object");
  if (!g_ui.scaler_class || !object_class) {
    static bool noted = false;
    if (!noted) {
      noted = true;
      Log("UI scale: classes not loaded yet (CanvasScaler=%p "
          "UnityEngine.Object=%p); trying again",
          g_ui.scaler_class, object_class);
    }
    return false;
  }
  g_ui.helper_class = FindClassAnywhere("Beyond.UI", "UICanvasScaleHelper");
  // UnityEngine.Object.FindObjectsOfType: prefer (Type, bool includeInactive),
  // then (Type)
  for (int want : {2, 1}) {
    void *iter = nullptr;
    while (const void *m = get_methods(object_class, &iter)) {
      const char *n = method_name(m);
      if (n && strcmp(n, "FindObjectsOfType") == 0 && !is_generic(m) &&
          static_cast<int>(param_count(m)) == want) {
        g_ui.find_objects = m;
        g_ui.find_args = want;
        break;
      }
    }
    if (g_ui.find_objects)
      break;
  }
  g_ui.m_get_ref =
      method_from_name(g_ui.scaler_class, "get_referenceResolution", 0);
  g_ui.m_set_ref =
      method_from_name(g_ui.scaler_class, "set_referenceResolution", 1);
  g_ui.m_get_mode = method_from_name(g_ui.scaler_class, "get_uiScaleMode", 0);
  g_ui.m_get_sf = method_from_name(g_ui.scaler_class, "get_scaleFactor", 0);
  g_ui.m_set_sf = method_from_name(g_ui.scaler_class, "set_scaleFactor", 1);
  g_ui.m_set_mode = method_from_name(g_ui.scaler_class, "set_uiScaleMode", 1);
  g_ui.m_get_fallback_dpi =
      method_from_name(g_ui.scaler_class, "get_fallbackScreenDPI", 0);
  g_ui.m_get_physical_unit =
      method_from_name(g_ui.scaler_class, "get_physicalUnit", 0);
  g_ui.f_scaler_canvas = get_field(g_ui.scaler_class, "m_Canvas");
  g_ui.canvas_class = FindClassAnywhere("UnityEngine", "Canvas");
  if (g_ui.canvas_class) {
    g_ui.m_c_root = method_from_name(g_ui.canvas_class, "get_isRootCanvas", 0);
    g_ui.m_c_mode = method_from_name(g_ui.canvas_class, "get_renderMode", 0);
    g_ui.m_c_get_sf = method_from_name(g_ui.canvas_class, "get_scaleFactor", 0);
    g_ui.m_c_set_sf = method_from_name(g_ui.canvas_class, "set_scaleFactor", 1);
  }
  g_ui.m_name = method_from_name(object_class, "get_name", 0);
  if (g_managed.resolve_icall)
    g_ui.get_dpi = reinterpret_cast<float (*)()>(
        g_managed.resolve_icall("UnityEngine.Screen::get_dpi()"));
  Log("UI scale: extras: Canvas class=%p isRootCanvas=%d renderMode=%d get/set "
      "scaleFactor=%d/%d Object.get_name=%d scaler.m_Canvas=%d "
      "set_uiScaleMode=%d fallbackDPI=%d physicalUnit=%d Screen.get_dpi=%d",
      g_ui.canvas_class, g_ui.m_c_root != nullptr, g_ui.m_c_mode != nullptr,
      g_ui.m_c_get_sf != nullptr, g_ui.m_c_set_sf != nullptr,
      g_ui.m_name != nullptr, g_ui.f_scaler_canvas != nullptr,
      g_ui.m_set_mode != nullptr, g_ui.m_get_fallback_dpi != nullptr,
      g_ui.m_get_physical_unit != nullptr, g_ui.get_dpi != nullptr);
  if (g_ui.helper_class) {
    g_ui.m_h_std_x = method_from_name(g_ui.helper_class,
                                      "get_standardHorizontalResolution", 0);
    g_ui.m_h_std_y = method_from_name(g_ui.helper_class,
                                      "get_standardVerticalResolution", 0);
    g_ui.m_h_force =
        method_from_name(g_ui.helper_class, "ForceCanvasUpdate", 0);
    g_ui.m_h_scale =
        method_from_name(g_ui.helper_class, "get_standardResolutionScale", 0);
    g_ui.f_h_use = get_field(g_ui.helper_class, "_useCustomStandardResolution");
    g_ui.f_h_res = get_field(g_ui.helper_class, "_customStandardResolution");
    g_ui.f_h_pc =
        get_field(g_ui.helper_class, "_customPCReferenceResolutionScale");
  }
  Log("UI scale: resolved CanvasScaler=%p helper=%p FindObjectsOfType(%d "
      "args)=%s get/set referenceResolution=%d/%d get/set scaleFactor=%d/%d "
      "uiScaleMode=%d helper getters=%d/%d fields=%d/%d ForceCanvasUpdate=%d",
      g_ui.scaler_class, g_ui.helper_class, g_ui.find_args,
      g_ui.find_objects ? "found" : "MISSING", g_ui.m_get_ref != nullptr,
      g_ui.m_set_ref != nullptr, g_ui.m_get_sf != nullptr,
      g_ui.m_set_sf != nullptr, g_ui.m_get_mode != nullptr,
      g_ui.m_h_std_x != nullptr, g_ui.m_h_std_y != nullptr,
      g_ui.f_h_use != nullptr, g_ui.f_h_res != nullptr,
      g_ui.m_h_force != nullptr);
  if (!g_ui.find_objects || !g_ui.m_get_ref || !g_ui.m_set_ref)
    return false;
  g_ui.resolved = true;
  return true;
}

// objects of one class, as raw managed pointers (null on failure)
size_t FindObjects(void *klass, void ***out_elements) {
  *out_elements = nullptr;
  const void *type = g_ui.class_get_type(klass);
  void *type_object = type ? g_ui.type_get_object(type) : nullptr;
  if (!type_object)
    return 0;
  bool include_inactive = true;
  void *args[2] = {type_object, &include_inactive};
  void *exception = nullptr;
  void *array =
      g_managed.runtime_invoke(g_ui.find_objects, nullptr, args, &exception);
  if (exception || !array)
    return 0;
  const size_t length = g_ui.array_length(array);
  if (length > 100000)
    return 0;
  *out_elements =
      reinterpret_cast<void **>(static_cast<char *>(array) +
                                0x20); // Il2CppArray: object header 16, bounds
                                       // 8, max_length 8, then the elements
  return length;
}

bool Invoke0(const void *method, void *object, void **result_boxed) {
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(method, object, nullptr, &exception);
  if (result_boxed)
    *result_boxed = boxed;
  return !exception;
}
const void *Unboxed(void *boxed) {
  if (!boxed)
    return nullptr;
  return g_managed.object_unbox
             ? g_managed.object_unbox(boxed)
             : static_cast<const char *>(boxed) + 2 * sizeof(void *);
}
bool ReadVec2(const void *getter, void *object, Vec2 *out) {
  void *boxed = nullptr;
  if (!getter || !Invoke0(getter, object, &boxed))
    return false;
  const Vec2 *v = static_cast<const Vec2 *>(Unboxed(boxed));
  if (!v)
    return false;
  *out = *v;
  return true;
}
bool ReadFloat(const void *getter, void *object, float *out) {
  void *boxed = nullptr;
  if (!getter || !Invoke0(getter, object, &boxed))
    return false;
  const float *v = static_cast<const float *>(Unboxed(boxed));
  if (!v)
    return false;
  *out = *v;
  return true;
}
bool ReadInt(const void *getter, void *object, int *out) {
  void *boxed = nullptr;
  if (!getter || !Invoke0(getter, object, &boxed))
    return false;
  const int *v = static_cast<const int *>(Unboxed(boxed));
  if (!v)
    return false;
  *out = *v;
  return true;
}
bool WriteVec2(const void *setter, void *object, Vec2 v) {
  void *args[] = {&v};
  void *exception = nullptr;
  g_managed.runtime_invoke(setter, object, args, &exception);
  return !exception;
}
bool WriteFloat(const void *setter, void *object, float v) {
  void *args[] = {&v};
  void *exception = nullptr;
  g_managed.runtime_invoke(setter, object, args, &exception);
  return !exception;
}
bool Near(float a, float b) { return a > b - 0.01f && a < b + 0.01f; }

// helper mode: the reading of a standard resolution getter that returns int or
// float
bool ReadStd(const void *getter, void *object, float *out) {
  if (!getter)
    return false;
  const void *rt = g_ui.ret_type ? g_ui.ret_type(getter) : nullptr;
  const int kind = (rt && g_ui.type_type)
                       ? g_ui.type_type(rt)
                       : 0; // IL2CPP_TYPE_I4 = 8, IL2CPP_TYPE_R4 = 12
  if (kind == 12)
    return ReadFloat(getter, object, out);
  int value = 0;
  if (!ReadInt(getter, object, &value))
    return false;
  *out = static_cast<float>(value);
  return true;
}
struct HelperOrig {
  float x, y;
  bool have;
};
HelperOrig g_helper_orig = {0, 0, false};

void ApplyUiScaleHelperMode(float factor) {
  if (!g_ui.helper_class || !g_ui.f_h_use || !g_ui.f_h_res) {
    static bool noted = false;
    if (!noted) {
      noted = true;
      Log("UI scale helper mode: UICanvasScaleHelper or its custom-resolution "
          "fields are missing; nothing to do");
    }
    return;
  }
  void **items = nullptr;
  const size_t count = FindObjects(g_ui.helper_class, &items);
  for (size_t i = 0; i < count; ++i) {
    void *helper = items[i];
    if (!helper)
      continue;
    Vec2 cur{0, 0};
    unsigned char use = 0;
    g_ui.field_get(helper, g_ui.f_h_use, &use);
    g_ui.field_get(helper, g_ui.f_h_res, &cur);
    if (!g_helper_orig.have) {
      float sx = 0, sy = 0;
      const bool ok = ReadStd(g_ui.m_h_std_x, helper, &sx) &&
                      ReadStd(g_ui.m_h_std_y, helper, &sy);
      g_helper_orig = {ok ? sx : 1920.0f, ok ? sy : 1080.0f, true};
      Log("UI scale helper mode: helper %p use=%u custom=(%.1f, %.1f) standard "
          "resolution read %s = (%.1f, %.1f)",
          helper, use, cur.x, cur.y, ok ? "ok" : "FAILED (assuming 1920x1080)",
          g_helper_orig.x, g_helper_orig.y);
    }
    const Vec2 want{g_helper_orig.x / factor, g_helper_orig.y / factor};
    if (use != 1 || !Near(cur.x, want.x) || !Near(cur.y, want.y)) {
      unsigned char one = 1;
      g_ui.field_set(helper, g_ui.f_h_use, &one);
      g_ui.field_set(helper, g_ui.f_h_res, const_cast<Vec2 *>(&want));
      if (g_ui.m_h_force)
        Invoke0(g_ui.m_h_force, helper, nullptr);
      const LONG w = InterlockedIncrement(&g_ui_writes);
      if (w <= 10)
        Log("UI scale helper mode: helper %p custom standard resolution set to "
            "(%.1f, %.1f), ForceCanvasUpdate %s",
            helper, want.x, want.y, g_ui.m_h_force ? "called" : "not found");
    }
  }
}

// the game object name of a component/object, ASCII-fied, for the log
const char *NameOf(void *object, char *out, size_t cap) {
  out[0] = 0;
  if (!object || !g_ui.m_name)
    return out;
  void *exception = nullptr;
  void *str =
      g_managed.runtime_invoke(g_ui.m_name, object, nullptr, &exception);
  if (exception || !str)
    return out;
  const int length = *reinterpret_cast<int *>(static_cast<char *>(str) + 0x10);
  const wchar_t *chars =
      reinterpret_cast<wchar_t *>(static_cast<char *>(str) + 0x14);
  if (length <= 0 || length > 200)
    return out;
  int n = WideCharToMultiByte(CP_UTF8, 0, chars, length, out,
                              static_cast<int>(cap) - 1, nullptr, nullptr);
  out[n > 0 ? n : 0] = 0;
  return out;
}

bool ReadBool(const void *getter, void *object, int *out) {
  void *boxed = nullptr;
  if (!getter || !Invoke0(getter, object, &boxed))
    return false;
  const unsigned char *v = static_cast<const unsigned char *>(Unboxed(boxed));
  if (!v)
    return false;
  *out = *v ? 1 : 0;
  return true;
}

// Root canvases that no CanvasScaler drives (a menu that only has a Canvas):
// their own scaleFactor is the lever.
void ApplyCanvases(float factor, const std::unordered_map<void *, bool> &driven,
                   bool *any_new, size_t *wrote_out) {
  if (!g_ui.canvas_class || !g_ui.m_c_root || !g_ui.m_c_get_sf ||
      !g_ui.m_c_set_sf)
    return;
  void **items = nullptr;
  const size_t count = FindObjects(g_ui.canvas_class, &items);
  std::unordered_map<void *, CanvasState> next;
  for (size_t i = 0; i < count; ++i) {
    void *canvas = items[i];
    if (!canvas)
      continue;
    int root = 0, mode = -1;
    ReadBool(g_ui.m_c_root, canvas, &root);
    ReadInt(g_ui.m_c_mode, canvas,
            &mode); // 0 ScreenSpaceOverlay, 1 ScreenSpaceCamera, 2 WorldSpace
    float sf = 0;
    if (!ReadFloat(g_ui.m_c_get_sf, canvas, &sf))
      continue;
    const bool is_driven = driven.count(canvas) != 0;
    CanvasState st{};
    auto it = g_canvases.find(canvas);
    const bool known = it != g_canvases.end();
    if (known)
      st = it->second;
    if (!known && InterlockedDecrement(&g_ui_log_budget) >= 0) {
      char name[96];
      Log("UI scale: new Canvas %p name='%s' root=%d renderMode=%d "
          "scaleFactor=%.3f driven-by-CanvasScaler=%d",
          canvas, NameOf(canvas, name, sizeof(name)), root, mode, sf,
          is_driven ? 1 : 0);
      *any_new = true;
    }
    if (root && !is_driven && (mode == 0 || mode == 1)) {
      if (!st.applied || !Near(sf, st.written_sf))
        st.orig_sf = sf; // what the game set is the original
      const float want = st.orig_sf * factor;
      if (!Near(sf, want) && WriteFloat(g_ui.m_c_set_sf, canvas, want)) {
        st.written_sf = want;
        ++*wrote_out;
        if (InterlockedDecrement(&g_ui_log_budget) >= 0) {
          char name[96];
          Log("UI scale: scaler-less root Canvas %p '%s' scaleFactor %.3f -> "
              "%.3f%s",
              canvas, NameOf(canvas, name, sizeof(name)), st.orig_sf, want,
              st.applied ? " (the game had changed it back)" : "");
        }
      } else {
        st.written_sf = sf;
      }
      st.applied = true;
    }
    next[canvas] = st;
  }
  g_canvases.swap(next);
}

void ApplyUiScale() {
  ReadUiConfig();
  const float factor = g_ui_factor;
  FindModules();
  if (!ResolveUi())
    return;
  if (g_ui_helper_mode) {
    ApplyUiScaleHelperMode(factor);
    return;
  }
  void **items = nullptr;
  const size_t count = FindObjects(g_ui.scaler_class, &items);
  const LONG scan = InterlockedIncrement(&g_ui_scans);
  std::unordered_map<void *, ScalerState> next;
  std::unordered_map<void *, bool> driven;
  size_t changed = 0;
  bool any_new = false;
  for (size_t i = 0; i < count; ++i) {
    void *scaler = items[i];
    if (!scaler)
      continue;
    int mode = -1;
    ReadInt(g_ui.m_get_mode, scaler,
            &mode); // 0 ConstantPixelSize, 1 ScaleWithScreenSize, 2
                    // ConstantPhysicalSize
    if (g_ui.f_scaler_canvas) {
      void *canvas = nullptr;
      g_ui.field_get(scaler, g_ui.f_scaler_canvas, &canvas);
      if (canvas)
        driven[canvas] = true;
    }
    ScalerState st{};
    auto it = g_scalers.find(scaler);
    const bool known = it != g_scalers.end();
    if (known)
      st = it->second;
    Vec2 ref{0, 0};
    float sf = 0;
    const bool have_ref = ReadVec2(g_ui.m_get_ref, scaler, &ref);
    const bool have_sf = ReadFloat(g_ui.m_get_sf, scaler, &sf);
    if (!have_ref && !have_sf)
      continue;
    // what the game wrote last is the "original"; our own previous write is
    // recognised and not mistaken for it
    bool game_rewrote = false;
    if (!st.applied || !Near(ref.x, st.written_ref.x) ||
        !Near(ref.y, st.written_ref.y)) {
      game_rewrote = st.applied && mode == 1;
      st.orig_ref = ref;
    }
    if (!st.applied || (mode != 2 && !Near(sf, st.written_sf))) {
      game_rewrote = game_rewrote || (st.applied && mode == 0);
      st.orig_sf = sf;
    }
    bool wrote = false;
    if (mode == 1 && have_ref && g_ui.m_set_ref) {
      const Vec2 want{st.orig_ref.x / factor, st.orig_ref.y / factor};
      if (!Near(ref.x, want.x) || !Near(ref.y, want.y)) {
        wrote = WriteVec2(g_ui.m_set_ref, scaler, want);
        st.written_ref = want;
      } else {
        st.written_ref = ref;
      }
      st.written_sf = sf;
    } else if (mode == 0 && have_sf && g_ui.m_set_sf) {
      const float want = st.orig_sf * factor;
      if (!Near(sf, want)) {
        wrote = WriteFloat(g_ui.m_set_sf, scaler, want);
        st.written_sf = want;
      } else {
        st.written_sf = sf;
      }
      st.written_ref = ref;
    } else if (mode == 2 && g_ui.m_set_mode && g_ui.m_set_sf) {
      // ConstantPhysicalSize: the scaler resets scaleFactor from the DPI every
      // frame, so it is switched to ConstantPixelSize and given dpi/unit *
      // factor (Unity's own formula)
      float dpi = g_ui.get_dpi ? g_ui.get_dpi() : 0.0f, fallback = 96.0f;
      ReadFloat(g_ui.m_get_fallback_dpi, scaler, &fallback);
      int unit = 3;
      ReadInt(
          g_ui.m_get_physical_unit, scaler,
          &unit); // Centimeters 0, Millimeters 1, Inches 2, Points 3, Picas 4
      static const float kTarget[] = {2.54f, 25.4f, 1.0f, 72.0f, 6.0f};
      const float base = (dpi == 0.0f ? fallback : dpi) /
                         kTarget[(unit >= 0 && unit <= 4) ? unit : 2];
      int zero = 0;
      void *mode_args[] = {&zero};
      void *exception = nullptr;
      g_managed.runtime_invoke(g_ui.m_set_mode, scaler, mode_args, &exception);
      wrote = !exception && WriteFloat(g_ui.m_set_sf, scaler, base * factor);
      st.was_physical = wrote;
      st.orig_sf = base;
      st.written_sf = base * factor;
      st.written_ref = ref;
      if (InterlockedDecrement(&g_ui_log_budget) >= 0)
        Log("UI scale: ConstantPhysicalSize scaler %p: dpi %.1f fallback %.1f "
            "unit %d -> base %.3f, switched to ConstantPixelSize with %.3f",
            scaler, dpi, fallback, unit, base, base * factor);
    } else {
      st.written_ref = ref;
      st.written_sf = sf;
    }
    st.applied = true;
    if (wrote)
      ++changed;
    if ((!known || game_rewrote) &&
        InterlockedDecrement(&g_ui_log_budget) >= 0) {
      char name[96];
      any_new = true;
      Log("UI scale: %s CanvasScaler %p '%s' mode=%d ref (%.1f x %.1f) -> "
          "(%.1f x %.1f), scaleFactor %.3f -> %.3f%s",
          known ? "REWRITTEN BY GAME:" : "new", scaler,
          NameOf(scaler, name, sizeof(name)), mode, st.orig_ref.x,
          st.orig_ref.y, st.written_ref.x, st.written_ref.y, st.orig_sf,
          st.written_sf, wrote ? " [written]" : " [unchanged]");
    }
    next[scaler] = st;
  }
  g_scalers.swap(next);
  size_t canvas_wrote = 0;
  if (g_flag_canvases)
    ApplyCanvases(factor, driven, &any_new, &canvas_wrote);
  if (any_new || changed || canvas_wrote)
    g_ui_fast_until =
        g_ui_pumps +
        8; // something is appearing: look every 250 ms for two seconds
  if (scan == 1 || ((changed || canvas_wrote) &&
                    InterlockedDecrement(&g_ui_log_budget) >= 0))
    Log("UI scale: scan %ld found %zu CanvasScaler(s) and %zu Canvas(es), "
        "wrote %zu scalers + %zu canvases (factor %.3f)",
        scan, count, g_canvases.size(), changed, canvas_wrote, factor);
}

// factor 1.0 = off: nothing is touched. When the factor goes back to 1.0 after
// having been on, the originals are written back once.
int g_last_w = 0, g_last_h = 0;
bool g_ui_was_on = false;
void EnsureUiScale() {
  const LONG n = InterlockedIncrement(&g_ui_pumps);
  if (!g_flag_legacy)
    return; // exp3: nothing the game owns is rewritten unless legacy_scaler=1
  g_ui_factor = g_ui_manual;
  if (n % 2 != 1 && g_ui_was_on && n > g_ui_fast_until)
    return; // a scan costs a scene-wide object search: every 500 ms while on,
            // every 250 ms right after something new appeared
  if (n % 20 == 1 || g_ui_was_on || g_ui_factor != 1.0f || n == 1) {
    ReadUiConfig();
    g_ui_factor = g_ui_manual;
  }
  if (g_ui_factor == 1.0f && !g_ui_was_on)
    return;
  if (g_ui_factor == 1.0f && g_ui_was_on) {
    // restore originals once
    if (g_ui.resolved && !g_ui_helper_mode) {
      void **items = nullptr;
      const size_t count = FindObjects(g_ui.scaler_class, &items);
      for (size_t i = 0; i < count; ++i) {
        auto it = g_scalers.find(items[i]);
        if (it == g_scalers.end())
          continue;
        int mode = -1;
        ReadInt(g_ui.m_get_mode, items[i], &mode);
        if (it->second.was_physical && g_ui.m_set_mode) {
          int two = 2;
          void *args[] = {&two};
          void *exception = nullptr;
          g_managed.runtime_invoke(g_ui.m_set_mode, items[i], args, &exception);
        } else if (mode == 1)
          WriteVec2(g_ui.m_set_ref, items[i], it->second.orig_ref);
        else if (mode == 0 && g_ui.m_set_sf)
          WriteFloat(g_ui.m_set_sf, items[i], it->second.orig_sf);
      }
      void **canvases = nullptr;
      const size_t ccount =
          g_ui.canvas_class ? FindObjects(g_ui.canvas_class, &canvases) : 0;
      for (size_t i = 0; i < ccount; ++i) {
        auto it = g_canvases.find(canvases[i]);
        if (it != g_canvases.end() && it->second.applied && g_ui.m_c_set_sf)
          WriteFloat(g_ui.m_c_set_sf, canvases[i], it->second.orig_sf);
      }
      Log("UI scale: factor back to 1.0, originals restored on %zu "
          "CanvasScaler(s)",
          count);
    }
    g_scalers.clear();
    g_canvases.clear();
    g_ui_was_on = false;
    return;
  }
  g_ui_was_on = true;
  ApplyUiScale();
}

void ForceRelayout(const char *why) {
  if (!ResolveUi() || !g_ui.helper_class || !g_ui.m_h_force)
    return;
  void **helpers = nullptr;
  const size_t n = FindObjects(g_ui.helper_class, &helpers);
  for (size_t i = 0; i < n; ++i)
    if (helpers[i])
      Invoke0(g_ui.m_h_force, helpers[i], nullptr);
  Log("UI: UICanvasScaleHelper.ForceCanvasUpdate called on %zu helper(s) (%s)",
      n, why);
}

// ===================== generic il2cpp reflection helpers (signatures, numeric
// calls) =====================
using TypeGetName = char *(__cdecl *)(const void *);
using MethodGetParam = const void *(__cdecl *)(const void *, unsigned);
using MethodGetParamName = const char *(__cdecl *)(const void *, unsigned);
using MethodIsInstance = bool(__cdecl *)(const void *);
using FieldGetType2 = const void *(__cdecl *)(void *);
using FieldStaticGet = void(__cdecl *)(void *, void *);
struct Refl {
  TypeGetName type_name = nullptr;
  MethodGetParam get_param = nullptr;
  MethodGetParamName param_name = nullptr;
  MethodIsInstance is_instance = nullptr;
  FieldGetType2 field_type = nullptr;
  FieldStaticGet static_get = nullptr;
  ClassGetMethods get_methods = nullptr;
  MethodGetName method_name = nullptr;
  MethodGetParamCount param_count = nullptr;
  ClassGetField get_field = nullptr;
  bool ok = false;
};
Refl g_refl;
bool ResolveRefl() {
  if (g_refl.ok)
    return true;
  HMODULE game = g_managed.game_assembly;
  if (!game)
    return false;
  g_refl.type_name = reinterpret_cast<TypeGetName>(
      GetProcAddress(game, "il2cpp_type_get_name"));
  g_refl.get_param = reinterpret_cast<MethodGetParam>(
      GetProcAddress(game, "il2cpp_method_get_param"));
  g_refl.param_name = reinterpret_cast<MethodGetParamName>(
      GetProcAddress(game, "il2cpp_method_get_param_name"));
  g_refl.is_instance = reinterpret_cast<MethodIsInstance>(
      GetProcAddress(game, "il2cpp_method_is_instance"));
  g_refl.field_type = reinterpret_cast<FieldGetType2>(
      GetProcAddress(game, "il2cpp_field_get_type"));
  g_refl.static_get = reinterpret_cast<FieldStaticGet>(
      GetProcAddress(game, "il2cpp_field_static_get_value"));
  g_refl.get_methods = reinterpret_cast<ClassGetMethods>(
      GetProcAddress(game, "il2cpp_class_get_methods"));
  g_refl.method_name = reinterpret_cast<MethodGetName>(
      GetProcAddress(game, "il2cpp_method_get_name"));
  g_refl.param_count = reinterpret_cast<MethodGetParamCount>(
      GetProcAddress(game, "il2cpp_method_get_param_count"));
  g_refl.get_field = reinterpret_cast<ClassGetField>(
      GetProcAddress(game, "il2cpp_class_get_field_from_name"));
  g_refl.ok = g_refl.type_name && g_refl.get_param && g_refl.is_instance &&
              g_refl.field_type && g_refl.static_get && g_refl.get_methods &&
              g_refl.method_name && g_refl.param_count && g_refl.get_field &&
              g_ui.ret_type && g_ui.type_type;
  return g_refl.ok;
}
int TypeKind(const void *type) {
  return (type && g_ui.type_type) ? g_ui.type_type(type) : 0;
} // BOOLEAN 2, I1..U2 4-7, I4 8, U4 9, I8 10, U8 11, R4 12, R8 13, STRING 14,
  // VOID 1
bool NumericKind(int k) { return k == 2 || (k >= 4 && k <= 13); }
std::string Sig(const void *m) {
  std::string out = std::string(g_refl.is_instance(m) ? "" : "static ");
  const void *rt = g_ui.ret_type(m);
  char *rn = rt ? g_refl.type_name(rt) : nullptr;
  out += (rn ? rn : "?");
  out += " ";
  out += g_refl.method_name(m);
  out += "(";
  const unsigned n = g_refl.param_count(m);
  for (unsigned i = 0; i < n; ++i) {
    const void *pt = g_refl.get_param(m, i);
    char *pn = pt ? g_refl.type_name(pt) : nullptr;
    const char *nm = g_refl.param_name ? g_refl.param_name(m, i) : nullptr;
    if (i)
      out += ", ";
    out += (pn ? pn : "?");
    out += " ";
    out += (nm ? nm : "");
  }
  out += ")";
  return out;
}
// every method called `name` of a class, all arities (the signatures are
// logged: the game's overloads are not known statically)
std::vector<const void *> MethodsNamed(void *klass, const char *name) {
  std::vector<const void *> found;
  void *iter = nullptr;
  while (const void *m = g_refl.get_methods(klass, &iter)) {
    const char *n = g_refl.method_name(m);
    if (n && strcmp(n, name) == 0)
      found.push_back(m);
  }
  return found;
}
void LogMethods(void *klass, const char *class_name, const char *name) {
  auto ms = MethodsNamed(klass, name);
  if (ms.empty()) {
    Log("signature: %s.%s: not found", class_name, name);
    return;
  }
  for (const void *m : ms)
    Log("signature: %s.%s", class_name, Sig(m).c_str());
}
union Slot {
  int32_t i;
  uint32_t u;
  int64_t l;
  float f;
  double d;
  unsigned char b;
};
// calls a static (or instance, with object) method whose parameters are all
// numeric/bool; missing arguments are 0/false. false when the shape is not
// supported.
bool CallNumeric(const void *method, void *object,
                 const std::vector<double> &in, double *out, bool *has_result) {
  if (!method)
    return false;
  const unsigned n = g_refl.param_count(method);
  if (n > 6)
    return false;
  Slot slots[6] = {};
  void *args[6] = {};
  for (unsigned i = 0; i < n; ++i) {
    const void *pt = g_refl.get_param(method, i);
    const int k = TypeKind(pt);
    if (!NumericKind(k))
      return false;
    const double v = i < in.size() ? in[i] : 0.0;
    switch (k) {
    case 2:
      slots[i].b = v != 0.0;
      break;
    case 4:
    case 5:
      slots[i].b = static_cast<unsigned char>(static_cast<int>(v));
      break;
    case 6:
    case 7:
      slots[i].i = static_cast<int16_t>(static_cast<int>(v));
      break;
    case 8:
    case 9:
      slots[i].i = static_cast<int32_t>(v);
      break;
    case 10:
    case 11:
      slots[i].l = static_cast<int64_t>(v);
      break;
    case 12:
      slots[i].f = static_cast<float>(v);
      break;
    case 13:
      slots[i].d = v;
      break;
    }
    args[i] = &slots[i];
  }
  const void *rt = g_ui.ret_type(method);
  const int rk = TypeKind(rt);
  if (rk != 1 && !NumericKind(rk))
    return false;
  void *exception = nullptr;
  void *boxed =
      g_managed.runtime_invoke(method, object, n ? args : nullptr, &exception);
  if (exception)
    return false;
  if (has_result)
    *has_result = rk != 1;
  if (rk == 1 || !boxed)
    return true;
  const void *raw = Unboxed(boxed);
  if (!raw)
    return false;
  double v = 0;
  switch (rk) {
  case 2:
  case 4:
  case 5:
    v = *static_cast<const unsigned char *>(raw);
    break;
  case 6:
    v = *static_cast<const int16_t *>(raw);
    break;
  case 7:
    v = *static_cast<const uint16_t *>(raw);
    break;
  case 8:
    v = *static_cast<const int32_t *>(raw);
    break;
  case 9:
    v = *static_cast<const uint32_t *>(raw);
    break;
  case 10:
    v = static_cast<double>(*static_cast<const int64_t *>(raw));
    break;
  case 11:
    v = static_cast<double>(*static_cast<const uint64_t *>(raw));
    break;
  case 12:
    v = *static_cast<const float *>(raw);
    break;
  case 13:
    v = *static_cast<const double *>(raw);
    break;
  }
  if (out)
    *out = v;
  return true;
}
// a static field (const fields too) read as a number
bool StaticNumber(void *klass, const char *name, double *out, int *kind) {
  void *field = g_refl.get_field(klass, name);
  if (!field)
    return false;
  const int k = TypeKind(g_refl.field_type(field));
  if (kind)
    *kind = k;
  if (!NumericKind(k))
    return false;
  Slot slot = {};
  g_refl.static_get(field, &slot);
  switch (k) {
  case 2:
  case 4:
  case 5:
    *out = slot.b;
    break;
  case 6:
    *out = static_cast<int16_t>(slot.i);
    break;
  case 7:
    *out = static_cast<uint16_t>(slot.u);
    break;
  case 8:
    *out = slot.i;
    break;
  case 9:
    *out = slot.u;
    break;
  case 10:
    *out = static_cast<double>(slot.l);
    break;
  case 11:
    *out = static_cast<double>(static_cast<uint64_t>(slot.l));
    break;
  case 12:
    *out = slot.f;
    break;
  case 13:
    *out = slot.d;
    break;
  }
  return true;
}
const void *StaticMethod(void *klass, const char *name, int params) {
  for (const void *m : MethodsNamed(klass, name))
    if (!g_refl.is_instance(m) &&
        static_cast<int>(g_refl.param_count(m)) == params)
      return m;
  return nullptr;
}

// ===================== UI size: the game's own input, set once
// ===================== UICanvasScaleHelper._customPCReferenceResolutionScale
// multiplies the reference resolution on PC (2400x1350 instead of 1920x1080 in
// the owner's logs = 1.25), which is why the same UI looks smaller on a PC
// build than on iOS at the same pixel size. Set once to original / f (auto: f =
// original, i.e. 1.0 = the phone value) and followed by the helper's own
// ForceCanvasUpdate. Knob B (ui_knob=std, or when A is verified to do nothing):
// _useCustomStandardResolution + _customStandardResolution = standard / f.
// Nothing else is written. Only if the game resets THAT field (or a new helper
// object appears) is it applied again, and each time is logged.
enum KnobPhase {
  kKnobWaiting,
  kKnobPcVerify,
  kKnobPcDone,
  kKnobStdVerify,
  kKnobStdDone,
  kKnobFailed,
  kKnobOff
};
KnobPhase g_knob = kKnobWaiting;
float g_pc_orig = 1.0f, g_pc_target = 1.0f;
int g_pc_kind = 1; // 1 float, 2 Vector2
float g_std_orig_res[2] = {0, 0};
unsigned char g_std_orig_use = 0;
float g_std_target[2] = {0, 0};
float g_f_used = 1.25f;
std::unordered_map<void *, Vec2> g_knob_before;
bool g_knob_before_valid = false;
Vec2 g_helper_std_before{0, 0};
float g_helper_scale_before = 0;
LONG g_knob_verify_at = 0, g_knob_resets = 0;
void *g_first_helper = nullptr;

bool HelperStd(void *helper, Vec2 *out, float *scale) {
  float sx = 0, sy = 0;
  const bool ok = ReadStd(g_ui.m_h_std_x, helper, &sx) &&
                  ReadStd(g_ui.m_h_std_y, helper, &sy);
  *out = {sx, sy};
  *scale = 0;
  if (g_ui.m_h_scale)
    ReadFloat(g_ui.m_h_scale, helper, scale);
  return ok;
}
void WritePc(void *helper, float v) {
  float two[2] = {v, v};
  g_ui.field_set(helper, g_ui.f_h_pc, two);
}
void ReadPc(void *helper, float *a) {
  float raw[2] = {1, 1};
  g_ui.field_get(helper, g_ui.f_h_pc, raw);
  *a = raw[0];
}
void WriteStd(void *helper, bool on) {
  unsigned char use = on ? 1 : 0;
  g_ui.field_set(helper, g_ui.f_h_use, &use);
  if (on)
    g_ui.field_set(helper, g_ui.f_h_res, g_std_target);
}
void CaptureBaseline() {
  g_knob_before.clear();
  void **scalers = nullptr;
  const size_t sn = FindObjects(g_ui.scaler_class, &scalers);
  for (size_t i = 0; i < sn; ++i) {
    int mode = -1;
    Vec2 ref{0, 0};
    ReadInt(g_ui.m_get_mode, scalers[i], &mode);
    if (mode == 1 && ReadVec2(g_ui.m_get_ref, scalers[i], &ref))
      g_knob_before[scalers[i]] = ref;
  }
  g_knob_before_valid = !g_knob_before.empty();
}
// true when the game moved: a helper getter, or a scaler we saw before
bool KnobEffective(void *helper, char *why, size_t cap) {
  Vec2 std_now{0, 0};
  float scale_now = 0;
  if (HelperStd(helper, &std_now, &scale_now) &&
      (std::fabs(std_now.x - g_helper_std_before.x) > 1.0f ||
       std::fabs(std_now.y - g_helper_std_before.y) > 1.0f ||
       std::fabs(scale_now - g_helper_scale_before) > 0.001f)) {
    snprintf(why, cap,
             "helper standard resolution (%.0f x %.0f, scale %.3f) -> (%.0f x "
             "%.0f, scale %.3f)",
             g_helper_std_before.x, g_helper_std_before.y,
             g_helper_scale_before, std_now.x, std_now.y, scale_now);
    return true;
  }
  void **scalers = nullptr;
  const size_t sn = FindObjects(g_ui.scaler_class, &scalers);
  for (size_t i = 0; i < sn; ++i) {
    auto it = g_knob_before.find(scalers[i]);
    if (it == g_knob_before.end())
      continue;
    Vec2 now{0, 0};
    if (ReadVec2(g_ui.m_get_ref, scalers[i], &now) &&
        std::fabs(now.x - it->second.x) > it->second.x * 0.01f) {
      snprintf(why, cap,
               "CanvasScaler reference (%.0f x %.0f) -> (%.0f x %.0f)",
               it->second.x, it->second.y, now.x, now.y);
      return true;
    }
  }
  return false;
}

// ---- every panel brings its own UICanvasScaleHelper. Written afterwards (the
// periodic scan) a new panel is first laid out with the game's size and then
// jumps. The helper's own Awake / OnEnable / Start are replaced (the
// MethodInfo's code pointer, which the engine reads when it sends the message;
// no code bytes are touched) by thunks that put the size into the helper's
// serialized fields first and then call the original, so the first layout of a
// new helper already has the held size.
constexpr int kLifeSlots = 8;
using LifeFn = void(__cdecl *)(void *, const void *);
using FieldOffsetFn2 = int(__cdecl *)(void *);
using ClassParentFn = void *(__cdecl *)(void *);
LifeFn g_life_orig[kLifeSlots] = {};
int g_off_use = -1, g_off_res = -1, g_off_pc = -1, g_life_slots = 0;
bool g_life_tried = false;
LONG g_life_calls = 0, g_life_primed = 0;
void PrimeHelper(void *self) {
  if (!self || g_off_use < 0 || g_off_res < 0)
    return;
  __try {
    if (*static_cast<void **>(self) != g_ui.helper_class)
      return;
    char *base = static_cast<char *>(self);
    if (g_knob == kKnobStdVerify || g_knob == kKnobStdDone) {
      *reinterpret_cast<unsigned char *>(base + g_off_use) = 1;
      float *res = reinterpret_cast<float *>(base + g_off_res);
      res[0] = g_std_target[0];
      res[1] = g_std_target[1];
      InterlockedIncrement(&g_life_primed);
    } else if ((g_knob == kKnobPcVerify || g_knob == kKnobPcDone) &&
               g_off_pc >= 0) {
      float *pc = reinterpret_cast<float *>(base + g_off_pc);
      pc[0] = g_pc_target;
      if (g_pc_kind == 2)
        pc[1] = g_pc_target;
      InterlockedIncrement(&g_life_primed);
    }
  } __except (EXCEPTION_EXECUTE_HANDLER) {
  }
}
template <int I> void __cdecl LifeThunk(void *self, const void *method) {
  InterlockedIncrement(&g_life_calls);
  PrimeHelper(self);
  if (g_life_orig[I])
    g_life_orig[I](self, method);
}
template <int... I>
std::array<LifeFn, sizeof...(I)> MakeLife(std::integer_sequence<int, I...>) {
  return {&LifeThunk<I>...};
}
const std::array<LifeFn, kLifeSlots> g_life_thunks =
    MakeLife(std::make_integer_sequence<int, kLifeSlots>{});

void InstallLifecycle() {
  if (g_life_tried || !g_ui.helper_class || !g_ui.f_h_use || !g_ui.f_h_res ||
      !g_refl.ok)
    return;
  g_life_tried = true;
  HMODULE game = g_managed.game_assembly;
  auto field_offset = reinterpret_cast<FieldOffsetFn2>(
      GetProcAddress(game, "il2cpp_field_get_offset"));
  auto class_parent = reinterpret_cast<ClassParentFn>(
      GetProcAddress(game, "il2cpp_class_get_parent"));
  if (!field_offset) {
    Log("UI size: lifecycle hook not installed: il2cpp_field_get_offset is "
        "missing");
    return;
  }
  g_off_use = field_offset(g_ui.f_h_use);
  g_off_res = field_offset(g_ui.f_h_res);
  g_off_pc = g_ui.f_h_pc ? field_offset(g_ui.f_h_pc) : -1;
  const unsigned char *base = reinterpret_cast<const unsigned char *>(game);
  const IMAGE_NT_HEADERS *nt = reinterpret_cast<const IMAGE_NT_HEADERS *>(
      base + reinterpret_cast<const IMAGE_DOS_HEADER *>(base)->e_lfanew);
  const uintptr_t lo = reinterpret_cast<uintptr_t>(base),
                  hi = lo + nt->OptionalHeader.SizeOfImage;
  std::string all_names;
  void *k = g_ui.helper_class;
  for (int depth = 0; k && depth < 5;
       ++depth, k = class_parent ? class_parent(k) : nullptr) {
    void *iter = nullptr;
    while (const void *m = g_refl.get_methods(k, &iter)) {
      const char *name = g_refl.method_name(m);
      if (!name)
        continue;
      if (depth == 0) {
        all_names += name;
        all_names += " ";
      }
      if (!(strcmp(name, "Awake") == 0 || strcmp(name, "OnEnable") == 0 ||
            strcmp(name, "Start") == 0))
        continue;
      if (!g_refl.is_instance(m) || g_refl.param_count(m) != 0 ||
          TypeKind(g_ui.ret_type(m)) != 1)
        continue; // only instance, no-argument, void messages (Start may be a
                  // coroutine)
      uintptr_t *code = reinterpret_cast<uintptr_t *>(const_cast<void *>(m));
      if (*code < lo || *code >= hi || g_life_slots >= kLifeSlots)
        continue;
      DWORD old = 0;
      if (!VirtualProtect(code, sizeof(*code), PAGE_READWRITE, &old))
        continue;
      g_life_orig[g_life_slots] = reinterpret_cast<LifeFn>(*code);
      *code = reinterpret_cast<uintptr_t>(g_life_thunks[g_life_slots]);
      VirtualProtect(code, sizeof(*code), old, &old);
      Log("UI size: lifecycle hook %d on %s (class depth %d), code at "
          "GameAssembly+0x%llx",
          g_life_slots, name, depth,
          static_cast<unsigned long long>(
              reinterpret_cast<uintptr_t>(g_life_orig[g_life_slots]) - lo));
      ++g_life_slots;
    }
  }
  Log("UI size: UICanvasScaleHelper methods: %s; field offsets use=%d res=%d "
      "pc=%d; %d lifecycle hook(s)",
      all_names.c_str(), g_off_use, g_off_res, g_off_pc, g_life_slots);
}

// what the UI looks like now: every CanvasScaler with its reference resolution
// and its canvas scale, every helper with its custom resolution; for the log, a
// few times per session
LONG g_state_logs = 0, g_state_due = 0;
void LogUiState(const char *when) {
  if (!g_ui.scaler_class || InterlockedIncrement(&g_state_logs) > 14)
    return;
  void **scalers = nullptr;
  const size_t n = FindObjects(g_ui.scaler_class, &scalers);
  Log("UI state (%s): %zu CanvasScaler(s), factor x%.3f, lifecycle hook ran "
      "%ld time(s), primed %ld helper(s)",
      when, n, g_f_used, g_life_calls, g_life_primed);
  size_t shown = 0;
  for (size_t i = 0; i < n && shown < 16; ++i) {
    int mode = -1;
    Vec2 ref{0, 0};
    ReadInt(g_ui.m_get_mode, scalers[i], &mode);
    ReadVec2(g_ui.m_get_ref, scalers[i], &ref);
    if (mode != 1)
      continue;
    void *canvas = nullptr;
    if (g_ui.f_scaler_canvas)
      g_ui.field_get(scalers[i], g_ui.f_scaler_canvas, &canvas);
    float csf = 0;
    if (canvas && g_ui.m_c_get_sf)
      ReadFloat(g_ui.m_c_get_sf, canvas, &csf);
    char name[96];
    Log("UI state: scaler '%s' reference %.0f x %.0f canvas scale %.3f",
        NameOf(scalers[i], name, sizeof(name)), ref.x, ref.y, csf);
    ++shown;
  }
  void **helpers = nullptr;
  const size_t hn =
      g_ui.helper_class ? FindObjects(g_ui.helper_class, &helpers) : 0;
  for (size_t i = 0; i < hn && i < 10; ++i) {
    unsigned char use = 0;
    Vec2 cur{0, 0}, sv{0, 0};
    float sc = 0;
    g_ui.field_get(helpers[i], g_ui.f_h_use, &use);
    g_ui.field_get(helpers[i], g_ui.f_h_res, &cur);
    HelperStd(helpers[i], &sv, &sc);
    Log("UI state: helper %p use=%u custom (%.0f x %.0f) standard getters "
        "(%.0f x %.0f) scale %.3f",
        helpers[i], use, cur.x, cur.y, sv.x, sv.y, sc);
  }
}

// the factor the ini asks for: off = 1.0 (the game's own size), a number =
// that, auto = the PC multiplier removed (the phone build's size; 1.25 when the
// game value is not readable)
float WantedFactor(float pc_orig) {
  // the factor relative to the PC default; in cloud mode the base is the game's
  // own phone size, which already is PC default x PC multiplier: the extra
  // factor is what is left
  const float base = g_cloud_active ? g_cloud_pc_scale : 1.0f;
  if (g_ui_off)
    return g_cloud_active ? 1.0f : 1.0f;
  float total;
  if (g_ui_rel > 0)
    total = g_ui_rel;
  else if (g_have_profile && g_profile.ui_scale > 0)
    total = g_profile.ui_scale;
  else
    total = pc_orig > 1.001f ? pc_orig : 1.25f;
  return g_cloud_active ? total / base : total;
}

void EnsureUiKnob() {
  if (g_knob == kKnobOff || g_knob == kKnobFailed || g_ui_knob_mode == 3)
    return;
  if (g_ui_off && g_knob == kKnobWaiting)
    return; // ui_scale=off from the start: nothing is touched
  if (g_cloud_mode && !g_cloud_fallback &&
      (!g_cloud_active || g_pumps < g_cloud_since + 12))
    return; // cloud mode: the base size is the game's phone size; measured only
            // after the slots were filled and the UI laid out again
  if (g_knob == kKnobWaiting && g_ui_rel <= 0 && !g_have_profile)
    return; // ui_scale=auto depends on the device profile: not decided before
            // it is known (no second jump)
  if (g_state_due && g_pumps >= g_state_due) {
    g_state_due = 0;
    FindModules();
    if (ResolveUi())
      LogUiState("settled");
  }
  FindModules();
  if (!ResolveUi())
    return;
  if (g_knob == kKnobWaiting && !g_life_tried && ResolveRefl())
    InstallLifecycle();
  if (!g_ui.helper_class || !g_ui.f_h_pc) {
    g_knob = kKnobFailed;
    Log("UI size: UICanvasScaleHelper or its _customPCReferenceResolutionScale "
        "field is missing; nothing is changed");
    return;
  }
  const bool settled = g_knob == kKnobPcDone || g_knob == kKnobStdDone;
  if (g_knob == kKnobWaiting ? false : (settled ? (g_pumps % 4 != 1) : false))
    return;
  void **helpers = nullptr;
  const size_t hn = FindObjects(g_ui.helper_class, &helpers);
  if (hn == 0)
    return; // the UI is not built yet: looked at again on the next pump
  void *first = helpers[0];

  if (g_knob == kKnobWaiting) {
    CaptureBaseline();
    float pc = 1.0f;
    ReadPc(first, &pc);
    const void *ftype =
        g_refl.ok || ResolveRefl() ? g_refl.field_type(g_ui.f_h_pc) : nullptr;
    g_pc_kind = (TypeKind(ftype) == 17) ? 2 : 1;
    g_pc_orig = pc;
    HelperStd(first, &g_helper_std_before, &g_helper_scale_before);
    unsigned char use = 0;
    g_ui.field_get(first, g_ui.f_h_use, &use);
    Vec2 cur{0, 0};
    g_ui.field_get(first, g_ui.f_h_res, &cur);
    g_std_orig_use = use;
    g_std_orig_res[0] = cur.x;
    g_std_orig_res[1] = cur.y;
    Log("UI size: ORIGINAL values: _customPCReferenceResolutionScale=%.4f "
        "(field type %s), _useCustomStandardResolution=%u, "
        "_customStandardResolution=(%.1f, %.1f), standard resolution getters = "
        "(%.0f x %.0f), standardResolutionScale=%.4f; %zu helper(s), %zu "
        "mode-1 CanvasScaler(s) seen",
        pc, g_pc_kind == 2 ? "Vector2" : "float", use, cur.x, cur.y,
        g_helper_std_before.x, g_helper_std_before.y, g_helper_scale_before, hn,
        g_knob_before.size());
    if (g_knob_before_valid) {
      size_t shown = 0;
      for (auto &kv : g_knob_before) {
        char name[96];
        Log("UI size: before: CanvasScaler %p '%s' reference (%.0f x %.0f)",
            kv.first, NameOf(kv.first, name, sizeof(name)), kv.second.x,
            kv.second.y);
        if (++shown >= 6)
          break;
      }
    }
    // f: how much bigger than the PC default the UI becomes. Auto = the PC
    // reference scale itself (the phone value); ui_scale=<f> is relative to the
    // PC default.
    g_f_used = WantedFactor(pc);
    if (g_ui_knob_mode == 2 || (g_ui_knob_mode == 0 && !(pc > 1.001f))) {
      if (g_ui_knob_mode == 0)
        Log("UI size: the PC reference scale is already <= 1.0; going straight "
            "to the standard-resolution knob");
      goto try_std;
    }
    g_pc_target = pc / g_f_used;
    for (size_t i = 0; i < hn; ++i)
      if (helpers[i])
        WritePc(helpers[i], g_pc_target);
    Log("UI size: SET once: _customPCReferenceResolutionScale %.4f -> %.4f (UI "
        "x%.3f relative to the PC default); now the helper's own "
        "ForceCanvasUpdate",
        pc, g_pc_target, g_f_used);
    for (size_t i = 0; i < hn; ++i)
      if (helpers[i] && g_ui.m_h_force)
        Invoke0(g_ui.m_h_force, helpers[i], nullptr);
    g_knob = kKnobPcVerify;
    g_knob_verify_at = g_pumps + 8;
    g_first_helper = first;
    return;
  try_std: {
    Vec2 std_now = g_helper_std_before;
    if (std_now.x <= 0 || std_now.y <= 0)
      std_now = {1920.0f, 1080.0f};
    g_std_target[0] = std_now.x / g_f_used;
    g_std_target[1] = std_now.y / g_f_used;
    for (size_t i = 0; i < hn; ++i)
      if (helpers[i])
        WriteStd(helpers[i], true);
    Log("UI size: SET once (knob B): custom standard resolution (%.0f x %.0f) "
        "-> (%.0f x %.0f), use=1 (UI x%.3f); now the helper's own "
        "ForceCanvasUpdate",
        std_now.x, std_now.y, g_std_target[0], g_std_target[1], g_f_used);
    for (size_t i = 0; i < hn; ++i)
      if (helpers[i] && g_ui.m_h_force)
        Invoke0(g_ui.m_h_force, helpers[i], nullptr);
    g_knob = kKnobStdVerify;
    g_knob_verify_at = g_pumps + 8;
    g_first_helper = first;
  }
    return;
  }

  if (g_knob == kKnobPcVerify || g_knob == kKnobStdVerify) {
    if (g_pumps < g_knob_verify_at)
      return;
    char why[200] = {};
    const bool pc_phase = g_knob == kKnobPcVerify;
    if (KnobEffective(first, why, sizeof(why))) {
      Log("UI size: %s EFFECTIVE: %s",
          pc_phase ? "knob A (PC reference scale)"
                   : "knob B (custom standard resolution)",
          why);
      g_knob = pc_phase ? kKnobPcDone : kKnobStdDone;
      g_notch_reeval = true; // the canvas width changed: the notch value is
                             // worked out from it again
      LogUiState("after the size was applied");
      g_state_due = static_cast<LONG>(g_pumps) + 80;
      return;
    }
    if (!g_knob_before_valid) {
      Log("UI size: %s set, effect NOT verifiable (no mode-1 CanvasScaler "
          "existed before and the helper getters did not move); the value "
          "stays",
          pc_phase ? "knob A" : "knob B");
      g_knob = pc_phase ? kKnobPcDone : kKnobStdDone;
      return;
    }
    if (pc_phase && g_ui_knob_mode != 1) {
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i])
          WritePc(helpers[i], g_pc_orig);
      Log("UI size: knob A did nothing measurable; "
          "_customPCReferenceResolutionScale put back to %.4f; trying knob B",
          g_pc_orig);
      Vec2 std_now = g_helper_std_before;
      if (std_now.x <= 0 || std_now.y <= 0)
        std_now = {1920.0f, 1080.0f};
      g_std_target[0] = std_now.x / g_f_used;
      g_std_target[1] = std_now.y / g_f_used;
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i])
          WriteStd(helpers[i], true);
      Log("UI size: SET once (knob B): custom standard resolution -> (%.0f x "
          "%.0f), use=1",
          g_std_target[0], g_std_target[1]);
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i] && g_ui.m_h_force)
          Invoke0(g_ui.m_h_force, helpers[i], nullptr);
      g_knob = kKnobStdVerify;
      g_knob_verify_at = g_pumps + 8;
      return;
    }
    for (size_t i = 0; i < hn; ++i)
      if (helpers[i]) {
        if (pc_phase)
          WritePc(helpers[i], g_pc_orig);
        else {
          unsigned char use = g_std_orig_use;
          g_ui.field_set(helpers[i], g_ui.f_h_use, &use);
          g_ui.field_set(helpers[i], g_ui.f_h_res, g_std_orig_res);
        }
      }
    Log("UI size: %s did nothing measurable; the original values were put "
        "back. No UI size knob worked on this build.",
        pc_phase ? "knob A" : "knob B");
    g_knob = kKnobFailed;
    return;
  }

  // settled: a changed ui_scale in the ini is applied once (from the ORIGINAL
  // values, not from the last written ones) ...
  const bool pc_knob = g_knob == kKnobPcDone;
  {
    const float want = WantedFactor(g_pc_orig);
    if (std::fabs(want - g_f_used) > 0.001f) {
      const float before = g_f_used;
      g_f_used = want;
      if (pc_knob) {
        g_pc_target = g_pc_orig / g_f_used;
        for (size_t i = 0; i < hn; ++i)
          if (helpers[i])
            WritePc(helpers[i], g_pc_target);
      } else {
        Vec2 std_now = g_helper_std_before;
        if (std_now.x <= 0 || std_now.y <= 0)
          std_now = {1920.0f, 1080.0f};
        g_std_target[0] = std_now.x / g_f_used;
        g_std_target[1] = std_now.y / g_f_used;
        for (size_t i = 0; i < hn; ++i)
          if (helpers[i])
            WriteStd(helpers[i], true);
      }
      Log("UI size: ui_scale changed live: x%.3f -> x%.3f relative to the PC "
          "default (%s %.4f); the helper's own ForceCanvasUpdate, once",
          before, g_f_used,
          pc_knob ? "pc reference scale" : "custom standard resolution",
          pc_knob ? g_pc_target : g_std_target[0]);
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i] && g_ui.m_h_force)
          Invoke0(g_ui.m_h_force, helpers[i], nullptr);
      g_notch_reeval = true;
      g_state_due = static_cast<LONG>(g_pumps) + 8;
      return;
    }
  }
  // ... and only if the game resets the one field, or a new helper object
  // appears, is it applied again (and logged)
  for (size_t i = 0; i < hn; ++i) {
    void *h = helpers[i];
    if (!h)
      continue;
    bool reset = false;
    if (pc_knob) {
      float v = 0;
      ReadPc(h, &v);
      reset = std::fabs(v - g_pc_target) > 0.001f;
      if (reset) {
        const bool first_seen = h != first || g_knob_resets > 0;
        if (InterlockedIncrement(&g_knob_resets) <= 40)
          Log("UI size: %s: _customPCReferenceResolutionScale was %.4f, set "
              "again to %.4f",
              first_seen ? "the game (or a new helper) reset it" : "reset", v,
              g_pc_target);
        WritePc(h, g_pc_target);
      }
    } else {
      unsigned char use = 0;
      Vec2 cur{0, 0};
      g_ui.field_get(h, g_ui.f_h_use, &use);
      g_ui.field_get(h, g_ui.f_h_res, &cur);
      reset = use != 1 || !Near(cur.x, g_std_target[0]) ||
              !Near(cur.y, g_std_target[1]);
      if (reset) {
        if (InterlockedIncrement(&g_knob_resets) <= 40)
          Log("UI size: the game (or a new helper) reset the custom standard "
              "resolution (use=%u, %.0f x %.0f); set again to (%.0f x %.0f)",
              use, cur.x, cur.y, g_std_target[0], g_std_target[1]);
        WriteStd(h, true);
      }
    }
    if (reset && g_ui.m_h_force)
      Invoke0(g_ui.m_h_force, h, nullptr);
  }
}

// ===================== read-only observation: names of new canvases / scalers
// (the ESC menu) =====================
std::unordered_map<void *, bool> g_seen_canvas, g_seen_scaler;
LONG g_observe_budget = 400;
void ObserveUi() {
  if (!g_flag_observe || g_observe_budget <= 0 || g_knob == kKnobWaiting)
    return;
  if (g_pumps % 8 != 3)
    return;
  FindModules();
  if (!ResolveUi())
    return;
  std::unordered_map<void *, bool> now_scaler, now_canvas;
  void **items = nullptr;
  size_t count = FindObjects(g_ui.scaler_class, &items);
  for (size_t i = 0; i < count; ++i) {
    void *sc = items[i];
    if (!sc)
      continue;
    now_scaler[sc] = true;
    if (g_seen_scaler.count(sc))
      continue;
    int mode = -1;
    Vec2 ref{0, 0};
    float sf = 0;
    ReadInt(g_ui.m_get_mode, sc, &mode);
    ReadVec2(g_ui.m_get_ref, sc, &ref);
    ReadFloat(g_ui.m_get_sf, sc, &sf);
    if (InterlockedDecrement(&g_observe_budget) >= 0) {
      char name[96];
      Log("observe: new CanvasScaler %p '%s' mode=%d reference (%.0f x %.0f) "
          "scaleFactor %.3f",
          sc, NameOf(sc, name, sizeof(name)), mode, ref.x, ref.y, sf);
    }
  }
  if (g_ui.canvas_class && g_ui.m_c_root && g_ui.m_c_mode) {
    count = FindObjects(g_ui.canvas_class, &items);
    for (size_t i = 0; i < count; ++i) {
      void *cv = items[i];
      if (!cv)
        continue;
      now_canvas[cv] = true;
      if (g_seen_canvas.count(cv))
        continue;
      int root = 0, mode = -1;
      ReadBool(g_ui.m_c_root, cv, &root);
      ReadInt(g_ui.m_c_mode, cv, &mode);
      float sf = 0;
      if (g_ui.m_c_get_sf)
        ReadFloat(g_ui.m_c_get_sf, cv, &sf);
      if (root && mode != 2 && InterlockedDecrement(&g_observe_budget) >= 0) {
        char name[96];
        Log("observe: new root Canvas %p '%s' renderMode=%d scaleFactor %.3f",
            cv, NameOf(cv, name, sizeof(name)), mode, sf);
      }
    }
  }
  g_seen_scaler.swap(now_scaler);
  g_seen_canvas.swap(now_canvas);
}

// ===================== notch through the game's own setting
// ===================== Beyond.GameSetting has a persisted "video notch
// padding" setting (ID_VIDEO_NOTCH_PADDING). Beyond.UI.NotchAdapter lays out
// the side UI from it (via DeviceInfoManager.NotchPadding*), and
// ON_NOTCH_PADDING_CHANGED tells the UI. The Runtime reads it, logs units,
// writes it ONCE from the device profile through the game's own setter and
// restores the original at session end.
enum NotchPhase { kNotchWait, kNotchDone, kNotchSkipped };
NotchPhase g_notch = kNotchWait;
bool g_notch_written = false, g_notch_restored = false;
double g_notch_orig = 0, g_notch_target = 0;
std::string g_notch_id_text;
bool g_notch_float = false;
const void *g_notch_setter = nullptr;
bool g_finalizing = false;
LONG g_notch_attempt = 0;
std::wstring g_notch_file;

void RegistryNotchScan(const char *label) {
  // PlayerPrefs live in HKCU\Software\<company>\<product> on Windows: list
  // values whose name mentions notch
  int hits = 0;
  struct Walker {
    static void Walk(HKEY key, const std::string &path, int depth, int *hits,
                     const char *label) {
      char name[512];
      for (DWORD i = 0;; ++i) {
        DWORD n = sizeof(name), type = 0, size = 0;
        if (RegEnumValueA(key, i, name, &n, nullptr, &type, nullptr, &size) !=
            ERROR_SUCCESS)
          break;
        std::string lower = name;
        for (char &c : lower)
          c = static_cast<char>(tolower(c));
        if (lower.find("notch") != std::string::npos) {
          unsigned char data[16] = {};
          DWORD dsz = sizeof(data);
          DWORD t2 = 0;
          n = sizeof(name);
          RegQueryValueExA(key, name, nullptr, &t2, data, &dsz);
          Log("registry (%s): HKCU\\%s  value '%s' type %lu size %lu data "
              "%02x%02x%02x%02x%02x%02x%02x%02x",
              label, path.c_str(), name, type, size, data[0], data[1], data[2],
              data[3], data[4], data[5], data[6], data[7]);
          ++*hits;
        }
      }
      if (depth <= 0)
        return;
      char sub[256];
      for (DWORD i = 0;; ++i) {
        DWORD n = sizeof(sub);
        if (RegEnumKeyExA(key, i, sub, &n, nullptr, nullptr, nullptr,
                          nullptr) != ERROR_SUCCESS)
          break;
        HKEY child = nullptr;
        if (RegOpenKeyExA(key, sub, 0, KEY_READ, &child) == ERROR_SUCCESS) {
          Walk(child, path + "\\" + sub, depth - 1, hits, label);
          RegCloseKey(child);
        }
      }
    }
  };
  HKEY root = nullptr;
  if (RegOpenKeyExA(HKEY_CURRENT_USER, "Software\\Hypergryph", 0, KEY_READ,
                    &root) == ERROR_SUCCESS) {
    Walker::Walk(root, "Software\\Hypergryph", 3, &hits, label);
    RegCloseKey(root);
  }
  if (hits == 0)
    Log("registry (%s): no value with 'notch' in its name under "
        "HKCU\\Software\\Hypergryph (depth 3)",
        label);
}

// ---- Beyond.GameSetting is a static class keyed by string setting ids:
// SetSettingValueFloat(string settingId, float value). The id for the current
// screen shape comes from GetCurVideoNotchPaddingId() (a string such as
// "video_notch_padding218": the aspect bucket is part of it). The first build
// passed a number for the id and the call raised.
std::string Utf8Of(void *str) {
  if (!str)
    return "";
  const int length = *reinterpret_cast<int *>(static_cast<char *>(str) + 0x10);
  const wchar_t *chars =
      reinterpret_cast<wchar_t *>(static_cast<char *>(str) + 0x14);
  if (length <= 0 || length > 200)
    return "";
  char out[256] = {};
  const int n = WideCharToMultiByte(CP_UTF8, 0, chars, length, out,
                                    sizeof(out) - 1, nullptr, nullptr);
  return std::string(out, n > 0 ? n : 0);
}
using StringNewFn = void *(__cdecl *)(const char *);
void *NewManagedString(const std::string &text) {
  static StringNewFn fn = nullptr;
  if (!fn && g_managed.game_assembly)
    fn = reinterpret_cast<StringNewFn>(
        GetProcAddress(g_managed.game_assembly, "il2cpp_string_new"));
  return fn ? fn(text.c_str()) : nullptr;
}
// the id of the notch setting for the current screen shape; the constant
// ID_VIDEO_NOTCH_PADDING (a string) when the method is not callable
std::string FetchNotchId(void *setting) {
  if (const void *m = StaticMethod(setting, "GetCurVideoNotchPaddingId", 0)) {
    void *exception = nullptr;
    void *result = g_managed.runtime_invoke(m, nullptr, nullptr, &exception);
    const std::string id = exception ? "" : Utf8Of(result);
    if (!id.empty())
      return id;
  }
  if (void *field = g_refl.get_field(setting, "ID_VIDEO_NOTCH_PADDING")) {
    void *value = nullptr;
    g_refl.static_get(field, &value);
    return Utf8Of(value);
  }
  return "";
}
bool CallSetFloat(const void *setter, const std::string &id, float value,
                  bool *accepted) {
  void *id_object = NewManagedString(id);
  if (!id_object || !setter)
    return false;
  float v = value;
  void *args[2] = {id_object, &v};
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(setter, nullptr, args, &exception);
  if (exception)
    return false;
  if (accepted) {
    const unsigned char *raw =
        static_cast<const unsigned char *>(Unboxed(boxed));
    *accepted = raw ? *raw != 0 : true;
  }
  return true;
}

// the scale factor of the menu root canvas ('UIRoot'): canvas units are screen
// pixels / this
float UiRootScale() {
  if (!g_ui.scaler_class || !g_ui.f_scaler_canvas || !g_ui.m_c_get_sf ||
      !g_ui.m_name)
    return 0.0f;
  void **scalers = nullptr;
  const size_t n = FindObjects(g_ui.scaler_class, &scalers);
  for (size_t i = 0; i < n; ++i) {
    char name[96];
    if (!scalers[i] ||
        strcmp(NameOf(scalers[i], name, sizeof(name)), "UIRoot") != 0)
      continue;
    void *canvas = nullptr;
    g_ui.field_get(scalers[i], g_ui.f_scaler_canvas, &canvas);
    float sf = 0;
    if (canvas && ReadFloat(g_ui.m_c_get_sf, canvas, &sf) && sf > 0.1f)
      return sf;
  }
  return 0.0f;
}

void NotchFileSave(double original, const std::string &id) {
  if (g_notch_file.empty())
    return;
  char text[320];
  const int n = snprintf(text, sizeof(text), "original=%.6f\nid=%s\n", original,
                         id.c_str());
  HANDLE f =
      CreateFileW(g_notch_file.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                  CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (f != INVALID_HANDLE_VALUE) {
    DWORD w = 0;
    WriteFile(f, text, n, &w, nullptr);
    CloseHandle(f);
  }
}

void RestoreNotch() {
  if (!g_notch_written || g_notch_restored || !g_notch_setter)
    return;
  g_notch_restored = true;
  bool accepted = false;
  const bool ok = CallSetFloat(g_notch_setter, g_notch_id_text,
                               static_cast<float>(g_notch_orig), &accepted);
  Log("notch: session end: restoring the original notch padding %.4f for '%s' "
      "through the game's setter: %s",
      g_notch_orig, g_notch_id_text.c_str(), ok ? "called" : "FAILED");
  if (ok && !g_notch_file.empty())
    DeleteFileW(g_notch_file.c_str());
  RegistryNotchScan("after restore");
}

// safe_area changed to off (or to no insets) while a value is written: the
// original goes back through the game's setter, once
void RestoreNotchLive(const char *why) {
  if (!g_notch_written || !g_notch_setter)
    return;
  bool accepted = false;
  const bool ok = CallSetFloat(g_notch_setter, g_notch_id_text,
                               static_cast<float>(g_notch_orig), &accepted);
  Log("notch: %s: the original notch padding %.4f was put back through the "
      "game's setter: %s",
      why, g_notch_orig, ok ? "called" : "FAILED");
  if (ok) {
    g_notch_written = false;
    if (!g_notch_file.empty())
      DeleteFileW(g_notch_file.c_str());
    ForceRelayout("the notch padding was restored");
  }
}

void EnsureNotch() {
  if (g_notch_reeval) {
    g_notch_reeval = false;
    if (g_notch == kNotchDone || g_notch == kNotchSkipped)
      g_notch = kNotchWait;
    g_notch_attempt = 0;
  }
  if (!g_flag_notch_write) {
    if (g_notch_written && g_pumps % 4 == 2)
      RestoreNotchLive("safe_area=off");
    if (g_notch_written == false && g_notch == kNotchDone)
      g_notch = kNotchWait;
    return;
  }
  if (g_notch != kNotchWait || !g_have_profile)
    return;
  if (g_pumps % 4 != 2)
    return;
  if (!ProfileHasInsets(g_profile)) {
    RestoreNotchLive("the profile has no side inset");
    g_notch = kNotchSkipped;
    Log("notch: the device profile '%s' has no side inset; the game's notch "
        "padding is left alone",
        g_profile.name.c_str());
    return;
  }
  FindModules();
  if (!ResolveUi() || !ResolveRefl())
    return;
  if (++g_notch_attempt > 600) {
    g_notch = kNotchSkipped;
    Log("notch: gave up waiting for Beyond.GameSetting after 150 s");
    return;
  }
  void *setting = FindClassAnywhere("Beyond", "GameSetting");
  void *helper_cls = FindClassAnywhere("Beyond.Gameplay", "GameSettingHelper");
  if (!setting || !helper_cls)
    return;
  // wait for the settings table
  if (const void *is_init = StaticMethod(setting, "get_tableIsInit", 0)) {
    double v = 0;
    bool has = false;
    if (!CallNumeric(is_init, nullptr, {}, &v, &has) || v == 0)
      return;
  }
  static bool logged_methods = false;
  if (!logged_methods) {
    logged_methods = true;
    for (const char *n :
         {"get_videoNotchPadding", "get_videoCachedNotchPadding",
          "GetCurVideoNotchPaddingId", "get_videoNotchPaddingSetting",
          "SetSettingValueFloat"})
      LogMethods(setting, "GameSetting", n);
    for (const char *n : {"GetGameSettingDefaultNotchPadding",
                          "GetGameSettingNotchPaddingFromCanvasPadding",
                          "GetGameSettingCanvasPaddingFromNotchPadding"})
      LogMethods(helper_cls, "GameSettingHelper", n);
  }
  const std::string id = FetchNotchId(setting);
  if (id.empty())
    return; // the setting id is not available yet: asked again

  double max_value = 0;
  int max_kind = 0;
  const bool have_max = StaticNumber(
      setting, "DEFAULT_MAX_VALUE_VIDEO_NOTCH_PADDING", &max_value, &max_kind);
  double cur = 0, cached = 0, def = 0;
  bool has = false;
  const void *get_pad = StaticMethod(setting, "get_videoNotchPadding", 0);
  const void *get_cached =
      StaticMethod(setting, "get_videoCachedNotchPadding", 0);
  const bool have_cur =
      get_pad && CallNumeric(get_pad, nullptr, {}, &cur, &has) && has;
  const bool have_cached =
      get_cached && CallNumeric(get_cached, nullptr, {}, &cached, &has) && has;
  const void *get_def =
      StaticMethod(helper_cls, "GetGameSettingDefaultNotchPadding", 0);
  const bool have_def =
      get_def && CallNumeric(get_def, nullptr, {}, &def, &has) && has;
  Log("notch: setting id '%s' (maximum %s%.1f); current "
      "videoNotchPadding=%s%.4f cached=%s%.4f default=%s%.4f",
      id.c_str(), have_max ? "" : "(unreadable) ", max_value,
      have_cur ? "" : "(unreadable) ", cur, have_cached ? "" : "(unreadable) ",
      cached, have_def ? "" : "(unreadable) ", def);

  // the geometry of the menu canvas: canvas units = screen pixels / its scale
  // factor
  float il = 0, ir = 0, it = 0, ib = 0;
  const int W = g_last_w, H = g_last_h;
  ComputeInsets(g_profile, W, H, &il, &ir, &it, &ib);
  const double side_px = (il + ir) / 2.0;
  const int long_side = W > H ? W : H, short_side = W > H ? H : W;
  float sf = UiRootScale();
  const char *sf_from = "the UIRoot canvas";
  if (sf <= 0.0f) {
    void **helpers = nullptr;
    Vec2 sv{0, 0};
    float sc = 0;
    if (g_ui.helper_class && FindObjects(g_ui.helper_class, &helpers) > 0 &&
        helpers[0] && HelperStd(helpers[0], &sv, &sc) && sv.y > 0 && sc > 0) {
      sf = static_cast<float>(short_side) / (sv.y * sc);
      sf_from = "the helper's standard resolution (UIRoot not found)";
    } else {
      sf = static_cast<float>(short_side) / 1080.0f;
      sf_from = "1080 (nothing else readable)";
    }
  }
  const double canvas_w = long_side / static_cast<double>(sf),
               canvas_pad = side_px / sf;
  const void *from_canvas = StaticMethod(
      helper_cls, "GetGameSettingNotchPaddingFromCanvasPadding", 2);
  const void *to_canvas = StaticMethod(
      helper_cls, "GetGameSettingCanvasPaddingFromNotchPadding", 2);
  double target = canvas_pad;
  bool converted = false;
  if (from_canvas) {
    bool h = false;
    double conv = 0;
    if (CallNumeric(from_canvas, nullptr, {canvas_pad, canvas_w}, &conv, &h) &&
        h) {
      target = conv;
      converted = true;
    }
  }
  double back_canvas = 0;
  bool have_back_canvas = false;
  if (to_canvas) {
    bool h = false;
    have_back_canvas =
        CallNumeric(to_canvas, nullptr, {target, canvas_w}, &back_canvas, &h) &&
        h;
  }
  if (have_max && max_value > 0 && target > max_value) {
    Log("notch: target %.4f is above the setting's maximum %.4f; clamped",
        target, max_value);
    target = max_value;
  }
  Log("notch: profile %s: side inset %.1f px on a %d x %d screen; canvas scale "
      "%.3f from %s -> canvas width %.1f, padding %.2f canvas units -> setting "
      "value %.4f (%s; the game's own inverse gives %s%.2f canvas units back)",
      g_profile.name.c_str(), side_px, W, H, sf, sf_from, canvas_w, canvas_pad,
      target, converted ? "converted by the game" : "NOT converted",
      have_back_canvas ? "" : "(n/a) ", back_canvas);

  const void *setter = nullptr;
  for (const void *m : MethodsNamed(setting, "SetSettingValueFloat"))
    if (!g_refl.is_instance(m) && g_refl.param_count(m) == 2) {
      setter = m;
      break;
    }
  if (!setter || !have_cur) {
    g_notch = kNotchSkipped;
    Log("notch: cannot write: setter %s, current value %s (see the signature "
        "lines above); nothing was changed",
        setter ? "found" : "NOT found", have_cur ? "readable" : "NOT readable");
    return;
  }
  // a previous session that did not restore: its file holds the real original
  wchar_t path[MAX_PATH] = {};
  GetModuleFileNameW(g_module, path, MAX_PATH);
  std::wstring dir = path;
  g_notch_file = dir.substr(0, dir.find_last_of(L"\\/") + 1) +
                 L"endfield-notch-original.txt";
  double original = g_notch_written ? g_notch_orig : cur;
  if (!g_notch_written) {
    std::string old = ReadWholeFile(g_notch_file);
    const size_t at = old.find("original=");
    if (at != std::string::npos) {
      original = atof(old.c_str() + at + 9);
      Log("notch: a previous session did not restore the notch padding; its "
          "original value %.4f is used as the original (current value %.4f)",
          original, cur);
    }
  }
  if (std::fabs(cur - target) < 0.0005) {
    g_notch = kNotchDone;
    Log("notch: the setting already holds %.4f; nothing to write", cur);
    return;
  }
  RegistryNotchScan("before write");
  NotchFileSave(original, id);
  g_notch_orig = original;
  g_notch_id_text = id;
  g_notch_target = target;
  g_notch_setter = setter;
  bool accepted = false;
  const bool ok =
      CallSetFloat(setter, id, static_cast<float>(target), &accepted);
  double back = 0, back_cached = 0;
  const bool have_back =
      get_pad && CallNumeric(get_pad, nullptr, {}, &back, &has) && has;
  const bool have_back_cached =
      get_cached && CallNumeric(get_cached, nullptr, {}, &back_cached, &has) &&
      has;
  g_notch_written = ok;
  g_notch = kNotchDone;
  Log("notch: WROTE %.4f for '%s' through %s: call %s, the game says %s; read "
      "back videoNotchPadding=%s%.4f cached=%s%.4f -> %s",
      target, id.c_str(), Sig(setter).c_str(),
      ok ? "returned" : "RAISED an exception",
      accepted ? "accepted" : "refused/false", have_back ? "" : "(unreadable) ",
      back, have_back_cached ? "" : "(unreadable) ", back_cached,
      (have_back && std::fabs(back - target) < 0.01)
          ? "in effect"
          : "NOT in effect (report this)");
  if (!(have_back && std::fabs(back - target) < 0.01)) {
    // the Setting<float> object behind the property: its class and methods, for
    // the next round
    if (const void *get_obj =
            StaticMethod(setting, "get_videoNotchPaddingSetting", 0)) {
      void *exception = nullptr;
      void *obj =
          g_managed.runtime_invoke(get_obj, nullptr, nullptr, &exception);
      auto obj_class = reinterpret_cast<void *(__cdecl *)(void *)>(
          GetProcAddress(g_managed.game_assembly, "il2cpp_object_get_class"));
      auto class_name = reinterpret_cast<const char *(__cdecl *)(void *)>(
          GetProcAddress(g_managed.game_assembly, "il2cpp_class_get_name"));
      if (obj && !exception && obj_class && class_name) {
        void *k = obj_class(obj);
        Log("notch: videoNotchPaddingSetting is an object of class %s",
            class_name(k));
        void *iter = nullptr;
        int shown = 0;
        while (const void *m = g_refl.get_methods(k, &iter))
          if (shown++ < 30)
            Log("notch: Setting method: %s", Sig(m).c_str());
      }
    }
  }
  RegistryNotchScan("after write");
  ForceRelayout("the notch padding setting was written");
}

// ===================== device profile (+ optional safe-area override)
// ===================== The profile (ini > host > table > fallback) is always
// computed and logged. The Screen.safeArea override is OFF unless
// safearea_override=1: in exp2 it was suspected of breaking taps.
struct RectF {
  float x, y, w, h;
};
using IntFn = int(__cdecl *)();
IntFn g_screen_width = nullptr, g_screen_height = nullptr;
bool g_safe_hooked = false, g_safe_hook_failed = false;
LONG g_safe_calls = 0;
RectF g_safe_last = {-1, -1, -1, -1};
LONG g_safe_hooked_at_pump = 0;
bool g_safe_noread_noted = false;

void __cdecl SafeAreaHook(RectF *out) {
  const int w = g_screen_width ? g_screen_width() : 0,
            h = g_screen_height ? g_screen_height() : 0;
  float l = 0, r = 0, t = 0, b = 0;
  ComputeInsets(g_profile, w, h, &l, &r, &t, &b);
  RectF rect{l, b, static_cast<float>(w) - l - r,
             static_cast<float>(h) - t - b};
  if (w <= 0 || h <= 0 || rect.w <= 0 || rect.h <= 0)
    rect = {0, 0, static_cast<float>(w), static_cast<float>(h)};
  if (out)
    *out = rect;
  const LONG calls = InterlockedIncrement(&g_safe_calls);
  if (calls <= 6 || rect.x != g_safe_last.x || rect.y != g_safe_last.y ||
      rect.w != g_safe_last.w || rect.h != g_safe_last.h) {
    if (calls <= 40)
      Log("safeArea READ BY THE GAME (call %ld): returned Rect(x %.0f, y %.0f, "
          "w %.0f, h %.0f) on a %d x %d screen",
          calls, rect.x, rect.y, rect.w, rect.h, w, h);
    g_safe_last = rect;
  }
}

void EnsureProfile() {
  static LONG k = 0;
  ++k;
  FindModules();
  ResolveUnity();
  if (!g_managed.resolve_icall)
    return;
  if (!g_screen_width) {
    g_screen_width = reinterpret_cast<IntFn>(
        g_managed.resolve_icall("UnityEngine.Screen::get_width()"));
    g_screen_height = reinterpret_cast<IntFn>(
        g_managed.resolve_icall("UnityEngine.Screen::get_height()"));
    if (!g_screen_width || !g_screen_height) {
      g_screen_width = nullptr;
      return;
    }
  }
  const int w = g_screen_width(), h = g_screen_height();
  if (w <= 0 || h <= 0)
    return;
  if (!g_have_profile || w != g_last_w || h != g_last_h || k % 20 == 1 ||
      g_force_profile) {
    g_force_profile = false;
    const Profile chosen = BuildProfile(w, h);
    if (!g_have_profile || !chosen.Same(g_profile) || w != g_last_w ||
        h != g_last_h) {
      float l = 0, r = 0, t = 0, b = 0;
      ComputeInsets(chosen, w, h, &l, &r, &t, &b);
      const int W = w > h ? w : h, H = w > h ? h : w;
      Log("device profile: source=%s device=%s class=%s stream %d x %d (aspect "
          "%.3f); insets %.1f/%.1f/%.1f/%.1f pt (left/right/top/bottom) on a "
          "%.0f pt wide screen = %.0f/%.0f/%.0f/%.0f px",
          chosen.source.c_str(), chosen.name.c_str(), chosen.cls.c_str(), w, h,
          H > 0 ? static_cast<double>(W) / H : 0.0,
          chosen.use_px ? 0.0f : chosen.l_pt,
          chosen.use_px ? 0.0f : chosen.r_pt,
          chosen.use_px ? 0.0f : chosen.t_pt,
          chosen.use_px ? 0.0f : chosen.b_pt, chosen.width_pt, l, r, t, b);
      if (chosen.use_px)
        Log("device profile: the insets were given directly in stream pixels "
            "(safe_left/right/top/bottom)");
      g_profile = chosen;
      g_have_profile = true;
      g_last_w = w;
      g_last_h = h;
    }
  }
  if (!g_flag_safe_override)
    return;
  if (g_safe_hooked && !g_safe_noread_noted && g_safe_calls == 0 &&
      g_pumps > g_safe_hooked_at_pump + 80) {
    g_safe_noread_noted = true;
    Log("safeArea: the override has been installed for 20 s and the game has "
        "NOT read the safe area since");
  }
  if (g_safe_hooked || g_safe_hook_failed || !ProfileHasInsets(g_profile))
    return;
  void *target = g_managed.resolve_icall(
      "UnityEngine.Screen::get_safeArea_Injected(UnityEngine.Rect&)");
  if (!target) {
    g_safe_hook_failed = true;
    Log("safeArea: icall get_safeArea_Injected not found; no override");
    return;
  }
  unsigned char *code = static_cast<unsigned char *>(target);
  Log("safeArea: get_safeArea_Injected at %p, first bytes %02x %02x %02x %02x "
      "%02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x %02x",
      target, code[0], code[1], code[2], code[3], code[4], code[5], code[6],
      code[7], code[8], code[9], code[10], code[11], code[12], code[13],
      code[14], code[15]);
  unsigned char patch[14] = {0xFF, 0x25, 0, 0, 0, 0};
  void *destination = reinterpret_cast<void *>(&SafeAreaHook);
  memcpy(patch + 6, &destination, 8);
  DWORD old = 0;
  if (!VirtualProtect(target, sizeof(patch), PAGE_EXECUTE_READWRITE, &old)) {
    g_safe_hook_failed = true;
    Log("safeArea: VirtualProtect failed (error %lu); no override",
        GetLastError());
    return;
  }
  memcpy(target, patch, sizeof(patch));
  VirtualProtect(target, sizeof(patch), old, &old);
  FlushInstructionCache(GetCurrentProcess(), target, sizeof(patch));
  g_safe_hooked = true;
  g_safe_hooked_at_pump = g_pumps;
  Log("safeArea: override installed (safearea_override=1)");
  ForceRelayout("the safe-area override was installed");
}

#include "endfield_cloud.inc"

void Pump() {
  if (g_finalizing) {
    RestoreNotch(); // the session was released: the original notch padding goes
                    // back through the game's own setter (on this, the game's
                    // main thread)
    return;
  }
  if (!g_shared || !g_shared->alive)
    return;
  const LONG n = InterlockedIncrement(&g_pumps);
  if (n == 1)
    Log("first pump runs on the game's main thread (tid %lu)",
        GetCurrentThreadId());
  {
    EnsureFrameRate();
  }
  if (g_shared->touch_ui_enabled) {
    {
      EnsureTouch();
    }
    {
      EnsureDrag();
    }
    pm::Tick(n);
    if (n == 1 || n % 20 == 1)
      ReadUiConfig();
    EnsureUiScale(); // legacy_scaler=1 only
    EnsureProfile();
    cl::Tick();
    EnsureUiKnob();
    EnsureNotch();
    ObserveUi();
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
      Log("the pump hit a structured exception 0x%08lx and skipped this tick",
          GetExceptionCode());
  }
}

// The fallback when the message hook never sees the pump message: the window's
// own procedure, replaced for the Runtime's one message (the window belongs to
// this process, so the replacement is allowed from any thread, and the message
// is handled on the window's thread like the hook would).
LRESULT CALLBACK PumpWndProc(HWND window, UINT message, WPARAM wparam,
                             LPARAM lparam) {
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
      Log("message hook call %ld: wparam=%llu message=0x%04x (pump message is "
          "0x%04x)",
          calls, static_cast<unsigned long long>(wparam),
          seen ? seen->message : 0u, g_pump_message);
    }
  }
  if (code == HC_ACTION && (wparam & PM_REMOVE)) {
    const MSG *message = reinterpret_cast<const MSG *>(lparam);
    pm::HoldFast();
    if (message && g_drop_touch_mouse) {
      // the mouse messages Windows makes from a touch carry the signature
      // 0xFF5157xx in their extra info; the touch itself reaches the game as
      // WM_POINTER
      const UINT m = message->message;
      if ((m == WM_LBUTTONDOWN || m == WM_LBUTTONUP || m == WM_LBUTTONDBLCLK ||
           m == WM_MOUSEMOVE) &&
          (static_cast<unsigned long>(GetMessageExtraInfo()) & 0xFFFFFF00ul) ==
              0xFF515700ul) {
        const LONG dropped = InterlockedIncrement(&g_dropped_mouse);
        if (dropped <= 6)
          Log("touch_mouse: removed message 0x%04x (touch-made mouse message "
              "#%ld)",
              m, dropped);
        const_cast<MSG *>(message)->message = WM_NULL;
      }
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
  const std::wstring map_name =
      std::wstring(endfield_shared::kMapPrefix) + std::to_wstring(pid);
  HANDLE mapping = nullptr;
  while (!mapping) {
    mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, map_name.c_str());
    if (!mapping)
      Sleep(100);
  }
  g_shared = static_cast<SharedState *>(
      MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedState)));
  if (!g_shared || g_shared->magic != endfield_shared::kMapMagic) {
    if (g_shared)
      UnmapViewOfFile(g_shared);
    CloseHandle(mapping);
    return 1;
  }
  OpenLog(g_shared->log_path);
  Log("EndfieldRuntime attached: touch_ui=%ld unlock=%ld target_fps=%ld",
      g_shared->touch_ui_enabled, g_shared->unlock_enabled,
      g_shared->target_fps);
  g_pump_message = RegisterWindowMessageW(L"Sparxie.Endfield.Pump");

  HWND window = nullptr;
  DWORD ticks_without_window = 0;
  LONG posts_logged = 0;
  while (g_shared->alive) {
    if (!IsWindow(window)) {
      WindowSearch search{pid, nullptr};
      EnumWindows(FindUnityWindow, reinterpret_cast<LPARAM>(&search));
      window = search.window;
      g_game_window = window;
      if (window) {
        const DWORD thread = GetWindowThreadProcessId(window, nullptr);
        if (thread != g_main_thread) {
          if (g_hook) {
            UnhookWindowsHookEx(g_hook);
            g_hook = nullptr;
          }
          g_main_thread = thread;
          g_hook =
              SetWindowsHookExW(WH_GETMESSAGE, GetMsgProc, nullptr, thread);
          Log("Unity window %p on thread %lu: message hook %s (error %lu)",
              static_cast<void *>(window), thread,
              g_hook ? "installed" : "FAILED", g_hook ? 0ul : GetLastError());
        }
      } else if (++ticks_without_window == 600) {
        Log("no Unity window after 60 s: the game has not shown one; still "
            "waiting");
      }
    }
    if (window && g_hook) {
      const BOOL posted = PostMessageW(window, g_pump_message, 0, 0);
      ++posts_logged;
      if (!posted || posts_logged <= 2)
        Log("pump message posted: %s (error %lu), hook calls so far %ld",
            posted ? "ok" : "FAILED", posted ? 0ul : GetLastError(),
            g_hook_calls);
      // Windows Unity needs 5 to 11 s to start pumping messages (loading), so
      // "never ran after 6 posts" (1.5 s) replaced the game's window procedure
      // in every session although the hook was fine. Only after 60 s now.
      if (posts_logged == 240 && !g_pumped_by_hook && g_subclassed != window) {
        // the hook is installed but never ran: take the window's procedure
        // instead
        SetLastError(0);
        g_original_proc = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(
            window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(PumpWndProc)));
        g_subclassed = g_original_proc ? window : nullptr;
        Log("the message hook has not run after 240 posts (60 s); window "
            "procedure %s (error %lu)",
            g_original_proc ? "replaced" : "NOT replaced",
            g_original_proc ? 0ul : GetLastError());
      }
    }
    Sleep(250);
  }
  if (g_notch_written && !g_notch_restored) {
    g_finalizing = true; // one last pump on the game's main thread restores the
                         // notch padding
    if (window && IsWindow(window) && g_pump_message)
      PostMessageW(window, g_pump_message, 0, 0);
    for (int i = 0; i < 12 && !g_notch_restored; ++i)
      Sleep(100);
  }
  Log("session released; Runtime stops (pumps=%ld, touch applications=%ld, "
      "notch %s)",
      g_pumps, g_touch_applies,
      g_notch_written
          ? (g_notch_restored
                 ? "restored"
                 : "NOT restored (the game's main thread did not run it)")
          : "untouched");
  if (g_hook)
    UnhookWindowsHookEx(g_hook);
  g_hook = nullptr;
  UnmapViewOfFile(g_shared);
  CloseHandle(mapping);
  return 0;
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
  if (reason == DLL_PROCESS_ATTACH) {
    g_module = instance;
    DisableThreadLibraryCalls(instance);
    HANDLE thread =
        CreateThread(nullptr, 0, RuntimeThread, nullptr, 0, nullptr);
    if (thread)
      CloseHandle(thread);
  }
  return TRUE;
}
