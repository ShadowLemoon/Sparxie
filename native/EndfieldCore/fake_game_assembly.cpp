// A stand-in for the game's GameAssembly.dll in the Runtime tests: just the IL2CPP exports the Runtime resolves, with a Beyond.DeviceInfo that holds an input type.
// It records which thread called ChangeInputType, so a test can prove the Runtime does it on the game's main thread.
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
struct Boxed {
  void *klass;
  void *monitor;
  int32_t value;
} g_boxed;
}  // namespace

EXPORT void *il2cpp_domain_get() { return &g_domain; }
EXPORT const void *il2cpp_domain_assembly_open(void *, const char *name) { return name && strcmp(name, "Common.Beyond") == 0 ? &g_assembly : nullptr; }
EXPORT const void *il2cpp_assembly_get_image(const void *) { return &g_image; }
EXPORT void *il2cpp_class_from_name(const void *, const char *space, const char *name) {
  return space && name && strcmp(space, "Beyond") == 0 && strcmp(name, "DeviceInfo") == 0 ? &g_class : nullptr;
}
EXPORT const void *il2cpp_class_get_method_from_name(void *klass, const char *name, int argc) {
  if (klass != &g_class || !name) return nullptr;
  if (strcmp(name, "ChangeInputType") == 0 && argc == 1) return &g_change_method;
  if (strcmp(name, "get_inputType") == 0 && argc == 0) return &g_get_method;
  return nullptr;
}
EXPORT void *il2cpp_runtime_invoke(const void *method, void *, void **args, void **exception) {
  if (exception) *exception = nullptr;
  if (method == &g_change_method) {
    InterlockedIncrement(&g_change_calls);
    if (GetCurrentThreadId() != g_main_thread) InterlockedIncrement(&g_off_thread_calls);
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
EXPORT void *il2cpp_object_unbox(void *object) { return &static_cast<Boxed *>(object)->value; }

// ---- what the fake game (and the test) use ----
EXPORT void fake_set_main_thread(DWORD thread) { g_main_thread = thread; }
EXPORT void fake_game_sets_input(int value) { g_input_type = value; }
EXPORT int fake_input() { return g_input_type; }
EXPORT int fake_change_calls() { return g_change_calls; }
EXPORT int fake_off_thread_calls() { return g_off_thread_calls; }
EXPORT int fake_get_calls() { return g_get_calls; }

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
