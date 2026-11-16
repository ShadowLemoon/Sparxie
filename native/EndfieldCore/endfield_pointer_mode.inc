// Pointer mode (included into endfield_runtime.cpp, inside its anonymous
// namespace, after the click-trace block, before Pump()).
//
// Problem: the game's UI runs on
// UnityEngine.InputSystem.UI.InputSystemUIInputModule. Its pointerBehavior
// decides how the pointers (mouse, pen, every finger) share pointer records.
// The game itself switches it between SingleUnifiedPointer (1) and
// AllPointersAsIs (2) while touches come in. With the unified record, a
// mouse-position event (Windows and the stream make them from touches) can
// overwrite the position of a finger that is still down, the release then finds
// the wrong object and the click never fires: the button shows its press
// feedback and does nothing.
//
// What this does, all of it on the game thread:
//   ui_pointers=all (default) | 0 | 1 | 2 | off    the value the module is held
//   at (all = 2 = UIPointerBehavior.AllPointersAsIs: every pointer keeps its
//   own record; a real mouse
//                                                  that is plugged in still
//                                                  works, it is just one more
//                                                  pointer).
// The pump re-asserts the value through the game's own setter as well (once per
// pump, cheap), which also covers modules created later.

namespace pm {

using FieldOffsetFn = int(__cdecl *)(void *);

struct State {
  bool tried = false, ok = false;
  void *klass = nullptr;
  const void *get_b = nullptr, *set_b = nullptr;
  int field_off = -1;
  unsigned char *setter_fn = nullptr;
  bool guard_tried = false;
  uintptr_t ga_lo = 0, ga_hi = 0;
};
State S;

volatile LONG g_guard_installed = 0;
LONG g_set_total = 0, g_blocked = 0, g_hold_fix = 0, g_logged_block = 0,
     g_logged_fix = 0;
void *g_cached_module = nullptr;
void *g_cached_class = nullptr;

struct Caller {
  uintptr_t ret;
  LONG count;
  LONG blocked;
  int last_value;
  bool logged;
};
constexpr int kMaxCallers = 32;
Caller g_callers[kMaxCallers];
volatile LONG g_caller_n = 0;

// ---- resolving
bool Resolve() {
  if (S.tried)
    return S.ok;
  if (!ResolveDrag())
    return false;
  FindModules();
  HMODULE game = g_managed.game_assembly;
  if (!game)
    return false;
  S.klass =
      FindClassIn("UnityEngine.InputSystem.UI", "InputSystemUIInputModule");
  if (!S.klass)
    return false; // the Input System is not loaded yet: asked again on the next
                  // pump
  S.tried = true;
  auto method_from_name = reinterpret_cast<MethodFromName>(
      GetProcAddress(game, "il2cpp_class_get_method_from_name"));
  auto field_from_name = reinterpret_cast<FieldFromNameFn>(
      GetProcAddress(game, "il2cpp_class_get_field_from_name"));
  auto field_offset = reinterpret_cast<FieldOffsetFn>(
      GetProcAddress(game, "il2cpp_field_get_offset"));
  if (!method_from_name) {
    Log("pointer: il2cpp_class_get_method_from_name is missing; the pointer "
        "mode is not touched");
    return false;
  }
  S.get_b = method_from_name(S.klass, "get_pointerBehavior", 0);
  S.set_b = method_from_name(S.klass, "set_pointerBehavior", 1);
  void *field =
      field_from_name ? field_from_name(S.klass, "m_PointerBehavior") : nullptr;
  S.field_off = (field && field_offset) ? field_offset(field) : -1;
  const unsigned char *base = reinterpret_cast<const unsigned char *>(game);
  const IMAGE_NT_HEADERS *nt = reinterpret_cast<const IMAGE_NT_HEADERS *>(
      base + reinterpret_cast<const IMAGE_DOS_HEADER *>(base)->e_lfanew);
  S.ga_lo = reinterpret_cast<uintptr_t>(base);
  S.ga_hi = S.ga_lo + nt->OptionalHeader.SizeOfImage;
  S.setter_fn =
      S.set_b ? *reinterpret_cast<unsigned char *const *>(S.set_b) : nullptr;
  Log("pointer: InputSystemUIInputModule=%p get_pointerBehavior=%d "
      "set_pointerBehavior=%d field m_PointerBehavior offset %d; setter code "
      "at GameAssembly+0x%llx",
      S.klass, S.get_b != nullptr, S.set_b != nullptr, S.field_off,
      S.setter_fn ? static_cast<unsigned long long>(
                        reinterpret_cast<uintptr_t>(S.setter_fn) - S.ga_lo)
                  : 0ull);
  S.ok = S.get_b && S.set_b;
  return S.ok;
}

// ---- reading and writing through the game's own accessors
int ReadValue(void *module) {
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(S.get_b, module, nullptr, &exception);
  if (exception || !boxed)
    return -1;
  const void *raw = g_managed.object_unbox
                        ? g_managed.object_unbox(boxed)
                        : static_cast<const char *>(boxed) + 2 * sizeof(void *);
  return raw ? *static_cast<const int *>(raw) : -1;
}
bool WriteValue(void *module, int value) {
  void *args[1] = {&value};
  void *exception = nullptr;
  g_managed.runtime_invoke(S.set_b, module, args, &exception);
  return exception == nullptr;
}

void *ActiveModule(void **event_system_out) {
  if (!g_drag.get_current)
    return nullptr;
  void *exception = nullptr;
  void *current = g_managed.runtime_invoke(g_drag.get_current, nullptr, nullptr,
                                           &exception);
  if (exception || !current)
    return nullptr;
  if (event_system_out)
    *event_system_out = current;
  static const void *getter = nullptr;
  if (!getter) {
    auto method_from_name = reinterpret_cast<MethodFromName>(GetProcAddress(
        g_managed.game_assembly, "il2cpp_class_get_method_from_name"));
    getter = method_from_name ? method_from_name(g_drag.es_class,
                                                 "get_currentInputModule", 0)
                              : nullptr;
  }
  if (!getter)
    return nullptr;
  void *module = g_managed.runtime_invoke(getter, current, nullptr, &exception);
  return exception ? nullptr : module;
}

// ---- the fallback, on every message the hook sees: a direct read of the field
// of the module found on the last pump
void HoldFast() {
  if (!g_cached_module || S.field_off < 0)
    return;
  const LONG want = g_pm_want;
  if (want < 0)
    return;
  __try {
    if (*static_cast<void **>(g_cached_module) != g_cached_class)
      return; // not the object it was: the next pump looks again
    int32_t *field = reinterpret_cast<int32_t *>(
        static_cast<char *>(g_cached_module) + S.field_off);
    if (*field != want) {
      *field = want;
      InterlockedIncrement(&g_hold_fix);
    }
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    g_cached_module = nullptr;
  }
}

LONG g_pm_ticks = 0;
void Tick(LONG pump_no) {
  if (!Resolve())
    return;
  ++g_pm_ticks;
  if (g_pm_want >= 0) {
    void *es = nullptr;
    void *module = ActiveModule(&es);
    std::vector<void *> modules;
    if (module && S.klass) {
      g_cached_module = module;
      g_cached_class = *static_cast<void **>(module);
      modules.push_back(module);
    }
    if (g_pm_ticks % 40 == 1) { // every ~10 s: any other module instance
      void **items = nullptr;
      const size_t n = DragFindObjects(S.klass, &items);
      for (size_t i = 0; i < n; ++i)
        if (items[i] && std::find(modules.begin(), modules.end(), items[i]) ==
                            modules.end())
          modules.push_back(items[i]);
    }
    for (void *m : modules) {
      const int value = ReadValue(m);
      if (value != g_pm_want) {
        const bool ok = WriteValue(m, static_cast<int>(g_pm_want));
        if (InterlockedIncrement(&g_logged_fix) <= 20)
          Log("pointer: module %p pointerBehavior was %d, set to %ld through "
              "the game's setter (%s)",
              m, value, g_pm_want, ok ? "ok" : "RAISED");
      }
    }
  }
}

} // namespace pm
