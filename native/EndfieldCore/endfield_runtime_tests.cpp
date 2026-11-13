// Tests of EndfieldRuntime inside a dummy Unity game, started through EndfieldCore (endfield_launch).
// No real game, no desktop session. Exit code 0 = all passed.
#include <cstdlib>

#include "endfield_core_abi.h"
#include "endfield_test_support.h"

namespace fs = std::filesystem;
using namespace endfield_test;

namespace {

// FakeUnityEndfield.exe has a UnityWndClass window and a message loop, and loads stand-ins for GameAssembly.dll / UnityPlayer.dll. It sets the input type to Keyboard when it starts
// and once a second afterwards (the game does that whenever keyboard or mouse input arrives) and resets the frame rate to 60; EndfieldRuntime must put Touch and the target frame rate
// back every time, with every managed call on the window's own thread.
struct UnityFixture {
  TempDir dir;
  fs::path game = dir.path / L"Endfield.exe";
  fs::path record = dir.path / L"record.txt";
  fs::path data = dir.path / L"data";
  UnityFixture(DWORD life_ms) {
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    const fs::path beside = fs::path(self).parent_path();
    fs::copy_file(beside / L"FakeUnityEndfield.exe", game);
    fs::copy_file(beside / L"FakeGameAssembly.dll", dir.path / L"GameAssembly.dll");
    fs::copy_file(beside / L"FakeUnityPlayer.dll", dir.path / L"UnityPlayer.dll");
    SetEnvironmentVariableW(L"ECL_FAKE_RECORD", record.c_str());
    SetEnvironmentVariableW(L"ECL_FAKE_LIFE_MS", std::to_wstring(life_ms).c_str());
    SetEnvironmentVariableW(L"SPARXIE_DATA_DIR", data.c_str());
  }
  ~UnityFixture() {
    SetEnvironmentVariableW(L"ECL_FAKE_RECORD", nullptr);
    SetEnvironmentVariableW(L"ECL_FAKE_LIFE_MS", nullptr);
    SetEnvironmentVariableW(L"SPARXIE_DATA_DIR", nullptr);
  }
  std::wstring Log() const { return ReadWide(data / L"logs" / L"endfield-runtime.log"); }
};

int Number(const std::wstring &record, const wchar_t *key) { return _wtoi(Field(record, key).c_str()); }

void RunUnityGame(UnityFixture &fx, int32_t touch_ui_enabled, int32_t unlock) {
  Job job;
  Session s = NewSession(fx.game);
  s.request.touch_ui_enabled = touch_ui_enabled;
  s.request.fps_unlock_enabled = unlock;
  s.request.target_fps = 144;
  CHECK(endfield_create_session(&s.request, &s.result, &s.handle) == ENDFIELD_OK);
  CHECK(endfield_attach_job(s.handle, job.handle, &s.result) == ENDFIELD_OK);
  CHECK(endfield_launch(s.handle, &s.result) == ENDFIELD_OK);
  CHECK(endfield_wait_game_exit(s.handle, 30000, &s.result) == ENDFIELD_OK);
  CHECK(WaitFile(fx.record, 5000));
  CHECK(endfield_release(s.handle) == ENDFIELD_OK);
}

void TestRuntimeKeepsTouchOnTheMainThread() {
  UnityFixture fx(7000);
  RunUnityGame(fx, 1, 1);
  const std::wstring record = ReadWide(fx.record);
  CHECK(Number(record, L"flips") >= 5);
  CHECK(Number(record, L"samples") >= 4);
  // the sample just before each flip back to Keyboard: Touch had been put back within the second (one slow tick at most)
  CHECK(Number(record, L"touch_before_flip") >= Number(record, L"samples") - 1);
  CHECK(Number(record, L"change_calls") >= 4);
  CHECK(Number(record, L"get_calls") >= Number(record, L"change_calls"));
  CHECK(Number(record, L"off_thread_calls") == 0);   // ChangeInputType ran on the window's thread, every time
  CHECK(Number(record, L"fps_off_thread_calls") == 0);
  CHECK(Number(record, L"fps_set_calls") >= 4);       // the frame rate was put back after each reset
  const std::wstring log = fx.Log();
  if (log.find(L"first pump runs on the game's main thread") == std::wstring::npos)
    std::wprintf(L"--- endfield-runtime.log ---\n%ls\n--- record ---\n%ls\n", log.c_str(), record.c_str());
  std::wprintf(L"    (Runtime pumped by the %ls)\n", log.find(L"window procedure replaced") != std::wstring::npos ? L"window procedure fallback" : L"message hook");
  CHECK(log.find(L"EndfieldRuntime attached: touch_ui=1") != std::wstring::npos);
  CHECK(log.find(L"message hook installed") != std::wstring::npos);
  CHECK(log.find(L"first pump runs on the game's main thread") != std::wstring::npos);
  CHECK(log.find(L"ChangeInputType(Touch) returned") != std::wstring::npos);
  CHECK(log.find(L"target frame rate was 60, set to 144") != std::wstring::npos);
  // the pointer mode: held at AllPointersAsIs (2) although the game writes SingleUnifiedPointer (1) every second; after the next message the hook has put it back
  CHECK(log.find(L"pointer: InputSystemUIInputModule") != std::wstring::npos);
  CHECK(Number(record, L"pointer_final") == 2);
  CHECK(Number(record, L"pointer_late") >= 1);
  CHECK(Number(record, L"pointer_late_wrong") == 0);
  // no endfield-ui.ini beside the Runtime: the shipped defaults
  CHECK(log.find(L"config: cloud_mode is on") != std::wstring::npos);
}

void TestRuntimeLeavesTheKeyboardUiAlone() {
  UnityFixture fx(3500);
  RunUnityGame(fx, 0, 1);  // 键鼠: the input type is the game's own
  const std::wstring record = ReadWide(fx.record);
  CHECK(Number(record, L"change_calls") == 0);
  CHECK(Number(record, L"input_final") == 0);
  CHECK(Number(record, L"pointer_final") == 1);       // the game's own pointer mode is left alone
  CHECK(Number(record, L"fps_final") == 144 || Number(record, L"fps_final") == 60);
  CHECK(fx.Log().find(L"EndfieldRuntime attached: touch_ui=0") != std::wstring::npos);
}

}  // namespace

int wmain() {
  Run("e2e: the Runtime holds Touch and the frame rate in a dummy Unity game, on its main thread, and logs it", TestRuntimeKeepsTouchOnTheMainThread);
  Run("e2e: with the keyboard input the Runtime leaves the input type alone", TestRuntimeLeavesTheKeyboardUiAlone);

  std::printf("%d tests, %d failed checks\n", g_run, g_failed);
  return g_failed ? 1 : 0;
}
