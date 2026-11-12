// The dummy Endfield.exe of the Runtime tests: a GUI program with a "UnityWndClass" window and a message loop, beside fake GameAssembly.dll / UnityPlayer.dll.
// Like the real game it sets the input type to Keyboard while it starts and again whenever "input" arrives, and it resets the frame rate. It records what the Runtime
// did (ECL_FAKE_RECORD) before it exits after ECL_FAKE_LIFE_MS: the final input type and frame rate, how often ChangeInputType ran and whether any call was off the main thread.
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <fstream>
#include <string>

namespace {
std::wstring Env(const wchar_t *name) {
  wchar_t buffer[2048] = {};
  const DWORD n = GetEnvironmentVariableW(name, buffer, 2048);
  return n && n < 2048 ? std::wstring(buffer, n) : std::wstring();
}
LRESULT CALLBACK Proc(HWND window, UINT message, WPARAM w, LPARAM l) { return DefWindowProcW(window, message, w, l); }

using SetThread = void(__cdecl *)(DWORD);
using SetValue = void(__cdecl *)(int);
using GetValue = int(__cdecl *)();
template <class T> T Sym(HMODULE module, const char *name) { return reinterpret_cast<T>(GetProcAddress(module, name)); }
}  // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
  wchar_t self[MAX_PATH] = {};
  GetModuleFileNameW(nullptr, self, MAX_PATH);
  std::wstring dir = self;
  dir.resize(dir.find_last_of(L'\\') + 1);
  HMODULE assembly = LoadLibraryW((dir + L"GameAssembly.dll").c_str());
  HMODULE unity = LoadLibraryW((dir + L"UnityPlayer.dll").c_str());
  if (!assembly || !unity) return 2;
  Sym<SetThread>(assembly, "fake_set_main_thread")(GetCurrentThreadId());
  Sym<SetThread>(unity, "fake_unity_set_main_thread")(GetCurrentThreadId());
  auto game_sets_input = Sym<SetValue>(assembly, "fake_game_sets_input");
  auto input = Sym<GetValue>(assembly, "fake_input");
  auto game_sets_fps = Sym<SetValue>(unity, "fake_game_sets_fps");

  WNDCLASSW cls{};
  cls.lpfnWndProc = Proc;
  cls.hInstance = instance;
  cls.lpszClassName = L"UnityWndClass";
  RegisterClassW(&cls);
  CreateWindowExW(0, L"UnityWndClass", L"Endfield", WS_OVERLAPPED, 0, 0, 64, 64, nullptr, nullptr, instance, nullptr);

  const DWORD life = static_cast<DWORD>(_wtoi(Env(L"ECL_FAKE_LIFE_MS").c_str()));
  const DWORD start = GetTickCount();
  DWORD next_flip = start + 600;  // the game's own start-up sets Keyboard
  int touch_before_flip = 0, flips = 0, samples = 0;
  std::wstring history;
  MSG message;
  while (GetTickCount() - start < (life ? life : 6000)) {
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
      TranslateMessage(&message);
      DispatchMessageW(&message);
    }
    if (GetTickCount() >= next_flip) {
      // the sample just before the game flips back: has the Runtime put Touch back since the last flip?
      if (flips > 0) {
        ++samples;
        if (input() == 1) ++touch_before_flip;
        history += std::to_wstring(input());
      }
      game_sets_input(0);   // keyboard or mouse input arrived: the game switches to Keyboard
      game_sets_fps(60);    // and keeps its own frame-rate setting
      ++flips;
      next_flip += 1000;
    }
    Sleep(5);
  }
  const std::wstring record = Env(L"ECL_FAKE_RECORD");
  if (!record.empty()) {
    const std::wstring tmp = record + L".tmp";
    {
      std::wofstream out(tmp, std::ios::binary);
      out << L"input_final=" << input() << L"\n";
      out << L"change_calls=" << Sym<GetValue>(assembly, "fake_change_calls")() << L"\n";
      out << L"off_thread_calls=" << Sym<GetValue>(assembly, "fake_off_thread_calls")() << L"\n";
      out << L"get_calls=" << Sym<GetValue>(assembly, "fake_get_calls")() << L"\n";
      out << L"fps_final=" << Sym<GetValue>(unity, "fake_fps")() << L"\n";
      out << L"fps_set_calls=" << Sym<GetValue>(unity, "fake_fps_set_calls")() << L"\n";
      out << L"fps_off_thread_calls=" << Sym<GetValue>(unity, "fake_fps_off_thread_calls")() << L"\n";
      out << L"flips=" << flips << L"\n";
      out << L"samples=" << samples << L"\n";
      out << L"touch_before_flip=" << touch_before_flip << L"\n";
      out << L"history=" << history << L"\n";
    }
    MoveFileExW(tmp.c_str(), record.c_str(), MOVEFILE_REPLACE_EXISTING);
  }
  return 0;
}
