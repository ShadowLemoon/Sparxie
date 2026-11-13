// Pointer mode (included into endfield_runtime.cpp, inside its anonymous namespace, before Pump()).
//
// The game's UI runs on UnityEngine.InputSystem.UI.InputSystemUIInputModule. Its pointerBehavior decides how the pointers (mouse, pen, every finger) share pointer records, and the game
// itself switches it between SingleUnifiedPointer (1) and AllPointersAsIs (2) while touches come in. With one shared record a mouse-position event (Windows makes them from touches, the
// stream moves the cursor too) can overwrite the position of a finger that is still down; the release then finds the wrong object and the click never fires: the button plays its press
// feedback and does nothing. The Runtime holds the value so that every pointer keeps its own record.
//
// endfield-ui.ini (next to the DLL, re-read about every 5 s):
//   ui_pointers=all (default) | 0 | 1 | 2 | off   the value to hold; all = 2 = AllPointersAsIs. A real mouse that is plugged in still works: it is one more pointer.
// Who changes it: the game writes the field itself (the setter has no call site in GameAssembly's code: the compiler inlined it where the game uses it), so a replacement of the setter would never see
// the change. The value is therefore held where it matters: on every window message the hook sees (a direct write of the field, no managed call, before the Input System reads the touch that comes with
// the message) and by the pump (4 times a second, through the game's own setter, which also finds a module that was created later).

namespace pm {

using FieldFromName = void *(__cdecl *)(void *, const char *);
using FieldOffsetFn = int(__cdecl *)(void *);
using DomainAssembliesFn = const void **(__cdecl *)(void *, size_t *);

// ini state: -1 = off, 0..2 = the value held
volatile LONG g_want = 2;

struct State {
  bool tried = false, ok = false;
  void *module_class = nullptr, *event_system_class = nullptr;
  const void *get_b = nullptr, *set_b = nullptr, *es_current = nullptr, *es_module = nullptr;
  int field_off = -1;
};
State S;

LONG g_hold_fix = 0, g_logged_fix = 0, g_last_fix_logged = 0, g_ticks = 0;
void *g_cached_module = nullptr;
void *g_cached_class = nullptr;

void *FindClass(const char *space, const char *name) {
  HMODULE game = g_managed.game_assembly;
  auto domain_get = reinterpret_cast<DomainGet>(GetProcAddress(game, "il2cpp_domain_get"));
  auto assembly_image = reinterpret_cast<AssemblyImage>(GetProcAddress(game, "il2cpp_assembly_get_image"));
  auto class_from_name = reinterpret_cast<ClassFromName>(GetProcAddress(game, "il2cpp_class_from_name"));
  auto assemblies = reinterpret_cast<DomainAssembliesFn>(GetProcAddress(game, "il2cpp_domain_get_assemblies"));
  if (!domain_get || !assembly_image || !class_from_name || !assemblies)
    return nullptr;
  size_t count = 0;
  const void **list = assemblies(domain_get(), &count);
  for (size_t i = 0; list && i < count && i < 4096; ++i) {
    const void *image = list[i] ? assembly_image(list[i]) : nullptr;
    if (void *klass = image ? class_from_name(image, space, name) : nullptr)
      return klass;
  }
  return nullptr;
}

bool Resolve() {
  if (S.tried)
    return S.ok;
  FindModules();
  HMODULE game = g_managed.game_assembly;
  if (!game || !g_managed.runtime_invoke)
    return false;
  S.module_class = FindClass("UnityEngine.InputSystem.UI", "InputSystemUIInputModule");
  S.event_system_class = FindClass("UnityEngine.EventSystems", "EventSystem");
  if (!S.module_class || !S.event_system_class)
    return false;  // the Input System is not loaded yet: asked again on the next pump
  S.tried = true;
  auto method_from_name = reinterpret_cast<MethodFromName>(GetProcAddress(game, "il2cpp_class_get_method_from_name"));
  auto field_from_name = reinterpret_cast<FieldFromName>(GetProcAddress(game, "il2cpp_class_get_field_from_name"));
  auto field_offset = reinterpret_cast<FieldOffsetFn>(GetProcAddress(game, "il2cpp_field_get_offset"));
  if (!method_from_name) {
    Log("pointer: il2cpp_class_get_method_from_name is missing; the pointer mode is not touched");
    return false;
  }
  S.get_b = method_from_name(S.module_class, "get_pointerBehavior", 0);
  S.set_b = method_from_name(S.module_class, "set_pointerBehavior", 1);
  S.es_current = method_from_name(S.event_system_class, "get_current", 0);
  S.es_module = method_from_name(S.event_system_class, "get_currentInputModule", 0);
  void *field = field_from_name ? field_from_name(S.module_class, "m_PointerBehavior") : nullptr;
  S.field_off = (field && field_offset) ? field_offset(field) : -1;
  S.ok = S.get_b && S.set_b && S.es_current && S.es_module;
  Log("pointer: InputSystemUIInputModule get=%d set=%d, EventSystem current=%d module=%d, field m_PointerBehavior offset %d; holding %ld", S.get_b != nullptr, S.set_b != nullptr, S.es_current != nullptr,
      S.es_module != nullptr, S.field_off, g_want);
  return S.ok;
}

// ---- the game's own accessors, on the game thread
int ReadValue(void *module) {
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(S.get_b, module, nullptr, &exception);
  if (exception || !boxed)
    return -1;
  const void *raw = g_managed.object_unbox ? g_managed.object_unbox(boxed) : static_cast<const char *>(boxed) + 2 * sizeof(void *);
  return raw ? *static_cast<const int *>(raw) : -1;
}

bool WriteValue(void *module, int value) {
  void *args[1] = {&value};
  void *exception = nullptr;
  g_managed.runtime_invoke(S.set_b, module, args, &exception);
  return exception == nullptr;
}

void *ActiveModule() {
  void *exception = nullptr;
  void *current = g_managed.runtime_invoke(S.es_current, nullptr, nullptr, &exception);
  if (exception || !current)
    return nullptr;
  void *module = g_managed.runtime_invoke(S.es_module, current, nullptr, &exception);
  return exception ? nullptr : module;
}

// ---- on every message the hook sees: a direct read of the field of the module found on the last pump
void HoldFast() {
  if (!g_cached_module || S.field_off < 0)
    return;
  const LONG want = g_want;
  if (want < 0)
    return;
  __try {
    if (*static_cast<void **>(g_cached_module) != g_cached_class)
      return;  // not the object it was: the next pump looks again
    int32_t *field = reinterpret_cast<int32_t *>(static_cast<char *>(g_cached_module) + S.field_off);
    if (*field != want) {
      *field = want;
      InterlockedIncrement(&g_hold_fix);
    }
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    g_cached_module = nullptr;
  }
}

void Tick() {
  if (g_want < 0 || !Resolve())
    return;
  ++g_ticks;
  if (void *module = ActiveModule()) {
    g_cached_module = module;
    g_cached_class = *static_cast<void **>(module);
    const int value = ReadValue(module);
    if (value != g_want) {
      const bool ok = WriteValue(module, static_cast<int>(g_want));
      if (InterlockedIncrement(&g_logged_fix) <= 20)
        Log("pointer: pointerBehavior of module %p was %d, set to %ld through the game's setter (%s)", module, value, g_want, ok ? "ok" : "RAISED");
    }
  }
  if (g_ticks % 40 == 0 && g_hold_fix != g_last_fix_logged) {
    Log("pointer: %ld corrections by the message hook so far; held value %ld", g_hold_fix, g_want);
    g_last_fix_logged = g_hold_fix;
  }
}

}  // namespace pm
