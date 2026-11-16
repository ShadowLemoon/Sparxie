// A stand-in for UnityPlayer.dll in the Runtime tests: il2cpp_resolve_icall for the two frame-rate calls.
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstring>

#define EXPORT extern "C" __declspec(dllexport)

namespace {
volatile LONG g_fps = -1;
volatile LONG g_set_calls = 0;
volatile LONG g_off_thread_calls = 0;
DWORD g_main_thread = 0;
void __cdecl SetTargetFrameRate(int value) {
  InterlockedIncrement(&g_set_calls);
  if (GetCurrentThreadId() != g_main_thread) InterlockedIncrement(&g_off_thread_calls);
  g_fps = value;
}
int __cdecl GetTargetFrameRate() { return g_fps; }
}  // namespace

EXPORT void *il2cpp_resolve_icall(const char *name) {
  if (!name) return nullptr;
  if (strcmp(name, "UnityEngine.Application::set_targetFrameRate(System.Int32)") == 0) return reinterpret_cast<void *>(&SetTargetFrameRate);
  if (strcmp(name, "UnityEngine.Application::get_targetFrameRate()") == 0) return reinterpret_cast<void *>(&GetTargetFrameRate);
  return nullptr;
}
EXPORT void fake_unity_set_main_thread(DWORD thread) { g_main_thread = thread; }
EXPORT void fake_game_sets_fps(int value) { g_fps = value; }
EXPORT int fake_fps() { return g_fps; }
EXPORT int fake_fps_set_calls() { return g_set_calls; }
EXPORT int fake_fps_off_thread_calls() { return g_off_thread_calls; }

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
