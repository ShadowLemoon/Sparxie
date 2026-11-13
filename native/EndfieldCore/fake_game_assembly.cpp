// A stand-in for the game's GameAssembly.dll in the Runtime tests: just the
// IL2CPP exports the Runtime resolves, with a Beyond.DeviceInfo that holds an
// input type. It records which thread called ChangeInputType, so a test can
// prove the Runtime does it on the game's main thread.
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstdint>
#include <cstring>

#define EXPORT extern "C" __declspec(dllexport)

namespace {
volatile LONG g_input_type = 0;
volatile LONG g_change_calls = 0;
volatile LONG g_off_thread_calls = 0;
volatile LONG g_get_calls = 0;
DWORD g_main_thread = 0;
int g_domain, g_assembly, g_image, g_class;
int g_change_method, g_get_method;
// the UI input module: an object with the field the game's setter stores to
// (offset 0x138, like the real class), the class of the EventSystem that hands
// it out
constexpr int kPointerField = 0x138;
struct ModuleObject {
  void *klass;
  unsigned char pad[kPointerField - sizeof(void *)];
  int32_t pointer_behavior;
} g_module_object;
int g_module_class, g_event_system_class, g_event_system_object, g_object_class,
    g_pointer_field_info, g_assemblies_dummy;
int g_get_pb_method, g_set_pb_method_unused, g_current_method,
    g_current_module_method, g_get_th_method, g_set_th_method,
    g_find_objects_method;
int g_find_method_info;
int g_event_system_threshold = 10;
struct FakeArray {
  unsigned char header[0x20];
  void *elements[1];
} g_objects;
struct MethodInfoLike {
  void *method_pointer;
} g_set_pb_method;
volatile LONG g_pointer_setter_calls = 0;
struct Boxed {
  void *klass;
  void *monitor;
  int32_t value;
} g_boxed;
} // namespace

// like the compiled C++ of IL2CPP: a plain field store, called directly by the
// game's own code
extern "C" __declspec(noinline) void FakeSetPointerBehavior(ModuleObject *self,
                                                            int32_t value) {
  self->pointer_behavior = value;
}

EXPORT void *il2cpp_domain_get() { return &g_domain; }
EXPORT const void **il2cpp_domain_get_assemblies(void *, size_t *count) {
  static const void *list[1] = {&g_assembly};
  *count = 1;
  return list;
}
EXPORT const void *il2cpp_domain_assembly_open(void *, const char *name) {
  return name && strcmp(name, "Common.Beyond") == 0 ? &g_assembly : nullptr;
}
EXPORT const void *il2cpp_assembly_get_image(const void *) { return &g_image; }
EXPORT void *il2cpp_class_from_name(const void *, const char *space,
                                    const char *name) {
  if (!space || !name)
    return nullptr;
  if (strcmp(space, "Beyond") == 0 && strcmp(name, "DeviceInfo") == 0)
    return &g_class;
  if (strcmp(space, "UnityEngine.InputSystem.UI") == 0 &&
      strcmp(name, "InputSystemUIInputModule") == 0)
    return &g_module_class;
  if (strcmp(space, "UnityEngine.EventSystems") == 0 &&
      strcmp(name, "EventSystem") == 0)
    return &g_event_system_class;
  if (strcmp(space, "UnityEngine") == 0 && strcmp(name, "Object") == 0)
    return &g_object_class;
  return nullptr;
}
EXPORT void *il2cpp_class_get_field_from_name(void *klass, const char *name) {
  return klass == &g_module_class && name &&
                 strcmp(name, "m_PointerBehavior") == 0
             ? &g_pointer_field_info
             : nullptr;
}
EXPORT int il2cpp_field_get_offset(void *field) {
  return field == &g_pointer_field_info ? kPointerField : -1;
}
EXPORT const void *
il2cpp_class_get_method_from_name(void *klass, const char *name, int argc) {
  if (!name)
    return nullptr;
  if (klass == &g_module_class) {
    if (strcmp(name, "get_pointerBehavior") == 0 && argc == 0)
      return &g_get_pb_method;
    if (strcmp(name, "set_pointerBehavior") == 0 && argc == 1) {
      g_set_pb_method.method_pointer =
          reinterpret_cast<void *>(&FakeSetPointerBehavior);
      return &g_set_pb_method;
    }
    return nullptr;
  }
  if (klass == &g_event_system_class) {
    if (strcmp(name, "get_current") == 0 && argc == 0)
      return &g_current_method;
    if (strcmp(name, "get_currentInputModule") == 0 && argc == 0)
      return &g_current_module_method;
    if (strcmp(name, "get_pixelDragThreshold") == 0 && argc == 0)
      return &g_get_th_method;
    if (strcmp(name, "set_pixelDragThreshold") == 0 && argc == 1)
      return &g_set_th_method;
    return nullptr;
  }
  if (klass != &g_class)
    return nullptr;
  if (strcmp(name, "ChangeInputType") == 0 && argc == 1)
    return &g_change_method;
  if (strcmp(name, "get_inputType") == 0 && argc == 0)
    return &g_get_method;
  return nullptr;
}
EXPORT const void *il2cpp_class_get_methods(void *klass, void **iter) {
  if (klass != &g_object_class || !iter || *iter)
    return nullptr;
  *iter = &g_find_method_info;
  return &g_find_method_info;
}
EXPORT const char *il2cpp_method_get_name(const void *method) {
  return method == &g_find_method_info ? "FindObjectsOfType" : "";
}
EXPORT unsigned il2cpp_method_get_param_count(const void *method) {
  return method == &g_find_method_info ? 2u : 0u;
}
EXPORT bool il2cpp_method_is_generic(const void *) { return false; }
EXPORT void il2cpp_field_get_value(void *, void *, void *) {}
EXPORT const void *il2cpp_field_get_type(void *) {
  return &g_pointer_field_info;
}
EXPORT int il2cpp_type_get_type(const void *) { return 8; }
EXPORT const void *il2cpp_method_get_return_type(const void *) {
  return &g_pointer_field_info;
}
EXPORT void il2cpp_field_static_get_value(void *, void *) {}
EXPORT const void *il2cpp_class_get_type(void *klass) { return klass; }
EXPORT void *il2cpp_type_get_object(const void *type) {
  return const_cast<void *>(type);
}
EXPORT size_t il2cpp_array_length(void *) { return 1; }

EXPORT void *il2cpp_runtime_invoke(const void *method, void *self, void **args,
                                   void **exception) {
  if (exception)
    *exception = nullptr;
  if (method == &g_current_method)
    return &g_event_system_object;
  if (method == &g_current_module_method) {
    g_module_object.klass = &g_module_class;
    return &g_module_object;
  }
  if (method == &g_find_objects_method) {
    g_objects.elements[0] = &g_event_system_object;
    return &g_objects;
  }
  if (method == &g_get_th_method) {
    g_boxed.value = g_event_system_threshold;
    return &g_boxed;
  }
  if (method == &g_set_th_method) {
    g_event_system_threshold = *static_cast<int32_t *>(args[0]);
    return nullptr;
  }
  if (method == &g_get_pb_method) {
    g_boxed.value = static_cast<ModuleObject *>(self)->pointer_behavior;
    return &g_boxed;
  }
  if (method == &g_set_pb_method) { // what the invoker does: call methodPointer
    InterlockedIncrement(&g_pointer_setter_calls);
    reinterpret_cast<void (*)(ModuleObject *, int32_t)>(
        g_set_pb_method.method_pointer)(static_cast<ModuleObject *>(self),
                                        *static_cast<int32_t *>(args[0]));
    return nullptr;
  }
  if (method == &g_change_method) {
    InterlockedIncrement(&g_change_calls);
    if (GetCurrentThreadId() != g_main_thread)
      InterlockedIncrement(&g_off_thread_calls);
    g_input_type = *static_cast<int32_t *>(args[0]);
    return nullptr;
  }
  if (method == &g_get_method) {
    InterlockedIncrement(&g_get_calls);
    g_boxed.value = g_input_type;
    return &g_boxed;
  }
  return nullptr;
}
EXPORT void *il2cpp_object_unbox(void *object) {
  return &static_cast<Boxed *>(object)->value;
}

// ---- what the fake game (and the test) use ----
EXPORT void fake_set_main_thread(DWORD thread) { g_main_thread = thread; }
EXPORT void fake_game_sets_input(int value) { g_input_type = value; }
EXPORT int fake_input() { return g_input_type; }
EXPORT int fake_change_calls() { return g_change_calls; }
EXPORT int fake_off_thread_calls() { return g_off_thread_calls; }
EXPORT int fake_get_calls() { return g_get_calls; }
// the game's own code writes the field itself (the setter is inlined at its
// call sites: it is never called)
EXPORT void fake_game_sets_pointer(int value) {
  g_module_object.pointer_behavior = value;
}
EXPORT int fake_pointer() { return g_module_object.pointer_behavior; }
EXPORT int fake_pointer_setter_calls() { return g_pointer_setter_calls; }

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
