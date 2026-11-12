// Tests of EndfieldCore's request validation and session guards (endfield_core.cpp). No game is started.
// Exit code 0 = all passed.
#include "endfield_core_abi.h"
#include "endfield_test_support.h"

namespace fs = std::filesystem;
using namespace endfield_test;

namespace {

void TestCreateSessionRejectsBadRequests() {
  TempDir dir;
  const fs::path game = dir.path / L"Endfield.exe";
  Session s = NewSession(game);
  CHECK(endfield_create_session(nullptr, &s.result, &s.handle) == ENDFIELD_ERR_INVALID_ARGUMENT);
  CHECK(endfield_create_session(&s.request, nullptr, &s.handle) == ENDFIELD_ERR_INVALID_ARGUMENT);
  CHECK(endfield_create_session(&s.request, &s.result, nullptr) == ENDFIELD_ERR_INVALID_ARGUMENT);

  Session wrong_abi = NewSession(game);
  wrong_abi.request.abi_version = ENDFIELD_ABI_VERSION + 1;
  CHECK(endfield_create_session(&wrong_abi.request, &wrong_abi.result, &wrong_abi.handle) == ENDFIELD_ERR_ABI_MISMATCH);
  Session too_small = NewSession(game);
  too_small.request.size = 47;  // one byte short of the first ABI size (48)
  CHECK(endfield_create_session(&too_small.request, &too_small.result, &too_small.handle) == ENDFIELD_ERR_ABI_MISMATCH);

  const auto rejects = [&](auto change) {
    Session bad = NewSession(game);
    change(bad.request);
    return endfield_create_session(&bad.request, &bad.result, &bad.handle) == ENDFIELD_ERR_CONFIG_INVALID;
  };
  CHECK(rejects([](EndfieldLaunchRequest &r) { r.target_fps = 9; }));
  CHECK(rejects([](EndfieldLaunchRequest &r) { r.target_fps = 1001; }));
  CHECK(rejects([](EndfieldLaunchRequest &r) { r.width = 319; }));
  CHECK(rejects([](EndfieldLaunchRequest &r) { r.height = 4321; }));
  CHECK(rejects([](EndfieldLaunchRequest &r) { r.refresh_rate = 23; }));
  CHECK(rejects([](EndfieldLaunchRequest &r) { r.game_executable_path_chars = 0; }));
  // neither the touch UI nor the frame-rate unlock: nothing to install
  CHECK(rejects([](EndfieldLaunchRequest &r) { r.fps_unlock_enabled = 0; r.touch_ui_enabled = 0; }));
}

void TestSessionGuards() {
  TempDir dir;
  Session s = NewSession(dir.path / L"Endfield.exe");
  CHECK(endfield_create_session(&s.request, &s.result, &s.handle) == ENDFIELD_OK);
  // the game file does not exist: the launch is refused before any process is created
  CHECK(endfield_launch(s.handle, &s.result) == ENDFIELD_ERR_PROCESS_NOT_FOUND);
  // not launched yet: there is no shared state to change and nothing to wait for
  CHECK(endfield_set_target_fps(s.handle, 144, &s.result) == ENDFIELD_ERR_INVALID_ARGUMENT);
  CHECK(endfield_wait_game_exit(s.handle, 0, &s.result) == ENDFIELD_ERR_SESSION_NOT_ACTIVE);
  CHECK(endfield_release(s.handle) == ENDFIELD_OK);
  CHECK(endfield_release(nullptr) == ENDFIELD_ERR_INVALID_ARGUMENT);
  CHECK(endfield_attach_job(nullptr, nullptr, &s.result) == ENDFIELD_ERR_SESSION_NOT_ACTIVE);
}

}  // namespace

int wmain() {
  Run("unit: bad requests are refused when the session is created", TestCreateSessionRejectsBadRequests);
  Run("unit: a session that was never launched refuses fps changes and waits", TestSessionGuards);

  std::printf("%d tests, %d failed checks\n", g_run, g_failed);
  return g_failed ? 1 : 0;
}
