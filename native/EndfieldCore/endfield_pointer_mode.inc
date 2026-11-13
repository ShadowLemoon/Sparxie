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
  void *klass = nullptr, *event_system_class = nullptr;
  const void *get_b = nullptr, *set_b = nullptr, *es_current = nullptr,
             *es_module = nullptr;
  int field_off = -1;
};
State S;

LONG g_hold_fix = 0, g_logged_fix = 0;
void *g_cached_module = nullptr;
void *g_cached_class = nullptr;

// ---- resolving
bool Resolve() {
  if (S.tried)
    return S.ok;
  FindModules();
  HMODULE game = g_managed.game_assembly;
  if (!game)
    return false;
  S.klass =
      FindClassIn("UnityEngine.InputSystem.UI", "InputSystemUIInputModule");
  S.event_system_class = FindClassIn("UnityEngine.EventSystems", "EventSystem");
  if (!S.klass || !S.event_system_class)
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
  S.es_current = method_from_name(S.event_system_class, "get_current", 0);
  S.es_module =
      method_from_name(S.event_system_class, "get_currentInputModule", 0);
  Log("pointer: InputSystemUIInputModule=%p EventSystem=%p get/set "
      "pointerBehavior=%d/%d field offset %d current/module=%d/%d",
      S.klass, S.event_system_class, S.get_b != nullptr, S.set_b != nullptr,
      S.field_off, S.es_current != nullptr, S.es_module != nullptr);
  S.ok = S.get_b && S.set_b && S.es_current && S.es_module;
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
  if (!S.es_current || !S.es_module)
    return nullptr;
  void *exception = nullptr;
  void *current =
      g_managed.runtime_invoke(S.es_current, nullptr, nullptr, &exception);
  if (exception || !current)
    return nullptr;
  if (event_system_out)
    *event_system_out = current;
  void *module =
      g_managed.runtime_invoke(S.es_module, current, nullptr, &exception);
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
void Tick(LONG) {
  if (g_pm_want < 0)
    return;
  if (!Resolve())
    return;
  ++g_pm_ticks;
  void *es = nullptr;
  void *module = ActiveModule(&es);
  std::vector<void *> modules;
  if (module && S.klass) {
    g_cached_module = module;
    g_cached_class = *static_cast<void **>(module);
    modules.push_back(module);
  }
  for (void *m : modules) {
    const int value = ReadValue(m);
    if (value != g_pm_want) {
      const bool ok = WriteValue(m, static_cast<int>(g_pm_want));
      if (InterlockedIncrement(&g_logged_fix) <= 20)
        Log("pointer: module %p pointerBehavior was %d, set to %ld through the "
            "game's setter (%s)",
            m, value, g_pm_want, ok ? "ok" : "RAISED");
    }
  }
}

} // namespace pm
