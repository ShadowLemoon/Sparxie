// Tests of the decoy-parent launch (endfield_decoy.cpp) and of EndfieldCore's use of it.
//   unit: every Win32 call replaced by a recording fake (attribute setup, failure paths, staging, release, sweep);
//   end to end: the real Win32 calls with a dummy game. FakeEndfield.exe (fake_endfield.cpp) copied as "Endfield.exe" is the dummy: it records
//   its parent pid and the parent's image path, and the test proves the parent is the staged Games.exe and that
//   both end with the session (game exit, or the session job closing).
// No real game, no desktop session: plain console processes. Exit code 0 = all passed.
#include <cstdlib>
#include <vector>

#include "endfield_core_abi.h"
#include "endfield_decoy.h"
#include "endfield_test_support.h"

namespace fs = std::filesystem;
using endfield::DecoyError;
using namespace endfield_test;

namespace {

std::wstring ImageBaseName() {
  wchar_t path[MAX_PATH] = {};
  GetModuleFileNameW(nullptr, path, MAX_PATH);
  return fs::path(path).filename().wstring();
}

std::wstring ImagePathOf(DWORD pid) {
  std::wstring out;
  if (HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)) {
    wchar_t path[1024] = {};
    DWORD n = 1024;
    if (QueryFullProcessImageNameW(h, 0, path, &n)) out.assign(path, n);
    CloseHandle(h);
  }
  return out;
}

// ---- fakes ------------------------------------------------------------------------------------------------------------

struct Calls {
  std::vector<std::string> log;
  bool delete_called = false;
  int delete_count = 0;
  int create_count = 0;
  DWORD attribute = 0;
  HANDLE attribute_value = nullptr;
  SIZE_T attribute_size = 0;
  std::wstring app, cmd, cwd;
  BOOL inherit = TRUE;
  DWORD flags = 0;
  DWORD startup_cb = 0;
  bool list_set = false;
};

endfield::Ops FakeOps(Calls &calls) {
  endfield::Ops ops;
  ops.verify_trust = [&](const fs::path &) -> LONG { calls.log.push_back("verify"); return 0; };
  ops.signer_hash = [&](const fs::path &, std::vector<uint8_t> &hash) { hash.assign(32, 7); return true; };
  ops.create_suspended = [&](const fs::path &, const fs::path &, PROCESS_INFORMATION *info) -> BOOL {
    calls.log.push_back("create_suspended");
    info->hProcess = reinterpret_cast<HANDLE>(0x100);
    info->hThread = reinterpret_cast<HANDLE>(0x104);
    info->dwProcessId = 4242;
    return TRUE;
  };
  ops.init_attr = [&](LPPROC_THREAD_ATTRIBUTE_LIST list, DWORD count, DWORD flags, PSIZE_T size) -> BOOL {
    calls.log.push_back(list ? "init" : "init_size");
    if (count != 1 || flags != 0) return FALSE;
    if (!list) {
      *size = 256;
      SetLastError(ERROR_INSUFFICIENT_BUFFER);
      return FALSE;
    }
    return TRUE;
  };
  ops.update_attr = [&](LPPROC_THREAD_ATTRIBUTE_LIST, DWORD flags, DWORD_PTR attribute, PVOID value, SIZE_T size, PVOID previous, PSIZE_T returned) -> BOOL {
    calls.log.push_back("update");
    calls.attribute = static_cast<DWORD>(attribute);
    calls.attribute_value = *static_cast<HANDLE *>(value);
    calls.attribute_size = size;
    return flags == 0 && !previous && !returned;
  };
  ops.delete_attr = [&](LPPROC_THREAD_ATTRIBUTE_LIST) { calls.delete_called = true; ++calls.delete_count; calls.log.push_back("delete"); };
  ops.create_process = [&](LPCWSTR app, LPWSTR cmd, BOOL inherit, DWORD flags, LPCWSTR cwd, STARTUPINFOEXW *startup, PROCESS_INFORMATION *info) -> BOOL {
    calls.log.push_back("create");
    ++calls.create_count;
    calls.app = app ? app : L"";
    calls.cmd = cmd ? cmd : L"";
    calls.cwd = cwd ? cwd : L"";
    calls.inherit = inherit;
    calls.flags = flags;
    calls.startup_cb = startup->StartupInfo.cb;
    calls.list_set = startup->lpAttributeList != nullptr;
    info->hProcess = reinterpret_cast<HANDLE>(0x200);
    info->hThread = reinterpret_cast<HANDLE>(0x204);
    info->dwProcessId = 5151;
    return TRUE;
  };
  ops.parent_pid_of = [&](HANDLE) -> DWORD { return 4242; };
  ops.terminate = [&](HANDLE h) { calls.log.push_back("terminate:" + std::to_string(reinterpret_cast<uintptr_t>(h))); };
  ops.wait = [&](HANDLE h, DWORD ms) { calls.log.push_back("wait:" + std::to_string(reinterpret_cast<uintptr_t>(h)) + ":" + std::to_string(ms)); };
  ops.close = [&](HANDLE h) { calls.log.push_back("close:" + std::to_string(reinterpret_cast<uintptr_t>(h))); };
  return ops;
}

endfield::Decoy FakeDecoy() {
  endfield::Decoy d;
  d.process = reinterpret_cast<HANDLE>(0x100);
  d.thread = reinterpret_cast<HANDLE>(0x104);
  d.pid = 4242;
  return d;
}

bool Has(const std::vector<std::string> &log, const std::string &entry) {
  for (const auto &e : log)
    if (e == entry) return true;
  return false;
}

void WriteFile(const fs::path &p, const std::string &text) {
  fs::create_directories(p.parent_path());
  std::ofstream(p, std::ios::binary) << text;
}

std::string ReadFile(const fs::path &p) {
  std::ifstream in(p, std::ios::binary);
  std::stringstream ss;
  ss << in.rdbuf();
  return ss.str();
}

std::wstring Lower(std::wstring s) {
  for (auto &c : s) c = static_cast<wchar_t>(towlower(c));
  return s;
}

// ---- unit tests: attribute setup and failure paths --------------------------------------------------------------------

void TestAttributeSetup() {
  Calls calls;
  const auto ops = FakeOps(calls);
  PROCESS_INFORMATION info{};
  DWORD seen = 0, os_error = 0;
  const auto result = endfield::StartWithParent(ops, FakeDecoy(), L"C:\\Games\\Endfield\\Endfield.exe", L"\"C:\\Games\\Endfield\\Endfield.exe\" -screen-width 1920", CREATE_SUSPENDED, info,
                                                &os_error, &seen);
  CHECK(result == DecoyError::ok);
  // one attribute, set once: PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = the decoy's process handle, pointer-sized
  CHECK(calls.attribute == 0x00020000u);
  CHECK(calls.attribute == PROC_THREAD_ATTRIBUTE_PARENT_PROCESS);
  CHECK(calls.attribute_value == reinterpret_cast<HANDLE>(0x100));
  CHECK(calls.attribute_size == sizeof(HANDLE));
  // the sizing call comes first, then the real init, then the update, then CreateProcessW, then the list is deleted exactly once
  CHECK(calls.log.size() >= 5);
  CHECK(calls.log[0] == "init_size" && calls.log[1] == "init" && calls.log[2] == "update" && calls.log[3] == "create" && calls.log[4] == "delete");
  CHECK(calls.delete_count == 1);
  // CreateProcessW(game, command line) in the game's folder, extended startup info, no handle inheritance, our flags kept
  CHECK(calls.app == L"C:\\Games\\Endfield\\Endfield.exe");
  CHECK(calls.cmd == L"\"C:\\Games\\Endfield\\Endfield.exe\" -screen-width 1920");
  CHECK(calls.cwd == L"C:\\Games\\Endfield");
  CHECK(calls.inherit == FALSE);
  CHECK((calls.flags & EXTENDED_STARTUPINFO_PRESENT) != 0);
  CHECK((calls.flags & CREATE_SUSPENDED) != 0);
  CHECK(calls.startup_cb == sizeof(STARTUPINFOEXW));
  CHECK(calls.list_set);
  CHECK(seen == 4242);
  CHECK(info.dwProcessId == 5151);
}

void TestInitFails() {
  Calls calls;
  auto ops = FakeOps(calls);
  ops.init_attr = [&](LPPROC_THREAD_ATTRIBUTE_LIST list, DWORD, DWORD, PSIZE_T size) -> BOOL {
    if (!list) {
      *size = 64;
      return FALSE;
    }
    SetLastError(ERROR_NOT_ENOUGH_MEMORY);
    return FALSE;
  };
  PROCESS_INFORMATION info{};
  DWORD os_error = 0;
  CHECK(endfield::StartWithParent(ops, FakeDecoy(), L"C:\\g\\Endfield.exe", L"x", 0, info, &os_error, nullptr) == DecoyError::attr_init);
  CHECK(os_error == ERROR_NOT_ENOUGH_MEMORY);
  CHECK(calls.create_count == 0);
  CHECK(!calls.delete_called);  // nothing was initialised, so nothing to delete
}

void TestUpdateFails() {
  Calls calls;
  auto ops = FakeOps(calls);
  ops.update_attr = [&](LPPROC_THREAD_ATTRIBUTE_LIST, DWORD, DWORD_PTR, PVOID, SIZE_T, PVOID, PSIZE_T) -> BOOL {
    SetLastError(ERROR_INVALID_PARAMETER);
    return FALSE;
  };
  PROCESS_INFORMATION info{};
  DWORD os_error = 0;
  CHECK(endfield::StartWithParent(ops, FakeDecoy(), L"C:\\g\\Endfield.exe", L"x", 0, info, &os_error, nullptr) == DecoyError::attr_update);
  CHECK(os_error == ERROR_INVALID_PARAMETER);
  CHECK(calls.create_count == 0);
  CHECK(calls.delete_count == 1);  // an initialised list is always deleted
}

void TestCreateFails() {
  for (const DWORD code : {static_cast<DWORD>(ERROR_ACCESS_DENIED), static_cast<DWORD>(ERROR_ELEVATION_REQUIRED)}) {
    Calls calls;
    auto ops = FakeOps(calls);
    ops.create_process = [&](LPCWSTR, LPWSTR, BOOL, DWORD, LPCWSTR, STARTUPINFOEXW *, PROCESS_INFORMATION *) -> BOOL {
      SetLastError(code);
      return FALSE;
    };
    PROCESS_INFORMATION info{};
    DWORD os_error = 0;
    const auto result = endfield::StartWithParent(ops, FakeDecoy(), L"C:\\g\\Endfield.exe", L"x", 0, info, &os_error, nullptr);
    CHECK(result == (code == ERROR_ELEVATION_REQUIRED ? DecoyError::create_game_elevation : DecoyError::create_game));
    CHECK(os_error == code);
    CHECK(calls.delete_count == 1);
  }
}

void TestParentMismatch() {
  Calls calls;
  auto ops = FakeOps(calls);
  ops.parent_pid_of = [&](HANDLE) -> DWORD { return 999; };
  PROCESS_INFORMATION info{};
  DWORD seen = 0, os_error = 0;
  CHECK(endfield::StartWithParent(ops, FakeDecoy(), L"C:\\g\\Endfield.exe", L"x", 0, info, &os_error, &seen) == DecoyError::parent_mismatch);
  CHECK(seen == 999);
  // the game that did not get the decoy as parent is terminated and its handles closed; the decoy is not touched
  CHECK(Has(calls.log, "terminate:" + std::to_string(0x200)));
  CHECK(Has(calls.log, "close:" + std::to_string(0x200)) && Has(calls.log, "close:" + std::to_string(0x204)));
  CHECK(!Has(calls.log, "terminate:" + std::to_string(0x100)));
}

// ---- unit tests: staging ----------------------------------------------------------------------------------------------

struct StageFixture {
  TempDir dir;
  fs::path game = dir.path / L"Endfield.exe";
  fs::path source = dir.path / L"PlatformProcess.exe";
  StageFixture() {
    WriteFile(game, "game");
    WriteFile(source, "platform");
  }
  fs::path StageRoot() const { return dir.path / L"_Hi3_Hg" / L"parent"; }
  bool NothingStaged() const {
    std::error_code ec;
    if (!fs::exists(StageRoot(), ec)) return true;
    for (const auto &e : fs::recursive_directory_iterator(StageRoot(), ec))
      if (e.is_regular_file()) return false;
    return true;
  }
};

void TestStageSuccess() {
  StageFixture fx;
  Calls calls;
  const auto ops = FakeOps(calls);
  endfield::Decoy decoy;
  DWORD os_error = 0;
  CHECK(endfield::StageDecoy(ops, fx.game, L"", true, decoy, &os_error) == DecoyError::ok);
  CHECK(decoy.pid == 4242);
  CHECK(decoy.staged.filename() == L"Games.exe");
  CHECK(ReadFile(decoy.staged) == "platform");
  // <game dir>\_Hi3_Hg\parent\<owner pid>\<guid>\Games.exe, the plugin's layout
  CHECK(decoy.staged.parent_path().parent_path().filename() == std::to_wstring(GetCurrentProcessId()));
  CHECK(decoy.staged.parent_path().parent_path().parent_path() == fx.StageRoot());
  CHECK(Has(calls.log, "create_suspended"));
  endfield::ReleaseDecoy(ops, decoy);
  CHECK(fx.NothingStaged());
  CHECK(!fs::exists(fx.StageRoot() / std::to_wstring(GetCurrentProcessId())));  // the empty owner folder goes too
}

void TestStageSourceMissing() {
  StageFixture fx;
  Calls calls;
  const auto ops = FakeOps(calls);
  endfield::Decoy decoy;
  fs::remove(fx.source);
  CHECK(endfield::StageDecoy(ops, fx.game, L"", true, decoy, nullptr) == DecoyError::source_missing);
  WriteFile(fx.dir.path / L"notexe.dll", "x");
  CHECK(endfield::StageDecoy(ops, fx.game, L"notexe.dll", true, decoy, nullptr) == DecoyError::source_missing);
  CHECK(!Has(calls.log, "create_suspended"));
  CHECK(fx.NothingStaged());
}

void TestStageConfiguredSource() {
  StageFixture fx;
  Calls calls;
  const auto ops = FakeOps(calls);
  WriteFile(fx.dir.path / L"sub" / L"Other.exe", "other");
  endfield::Decoy relative;
  CHECK(endfield::StageDecoy(ops, fx.game, L"sub\\Other.exe", true, relative, nullptr) == DecoyError::ok);
  CHECK(ReadFile(relative.staged) == "other");  // relative to the game folder
  endfield::Decoy absolute;
  CHECK(endfield::StageDecoy(ops, fx.game, (fx.dir.path / L"sub" / L"Other.exe").wstring(), true, absolute, nullptr) == DecoyError::ok);
  CHECK(ReadFile(absolute.staged) == "other");
  CHECK(relative.dir != absolute.dir);  // each session has its own folder
  endfield::ReleaseDecoy(ops, relative);
  endfield::ReleaseDecoy(ops, absolute);
}

void TestStageSignerChecks() {
  {  // the game's own signature is invalid
    StageFixture fx;
    Calls calls;
    auto ops = FakeOps(calls);
    ops.verify_trust = [&](const fs::path &p) -> LONG { return p.filename() == L"Endfield.exe" ? static_cast<LONG>(0x800B0100) : 0; };
    endfield::Decoy decoy;
    CHECK(endfield::StageDecoy(ops, fx.game, L"", true, decoy, nullptr) == DecoyError::signer_game);
    CHECK(!Has(calls.log, "create_suspended") && fx.NothingStaged());
  }
  {  // the source's signature is invalid
    StageFixture fx;
    Calls calls;
    auto ops = FakeOps(calls);
    ops.verify_trust = [&](const fs::path &p) -> LONG { return p.filename() == L"PlatformProcess.exe" ? static_cast<LONG>(0x800B0100) : 0; };
    endfield::Decoy decoy;
    CHECK(endfield::StageDecoy(ops, fx.game, L"", true, decoy, nullptr) == DecoyError::signer_source);
    CHECK(!Has(calls.log, "create_suspended") && fx.NothingStaged());
  }
  {  // different signer certificates
    StageFixture fx;
    Calls calls;
    auto ops = FakeOps(calls);
    ops.signer_hash = [&](const fs::path &p, std::vector<uint8_t> &hash) { hash.assign(32, p.filename() == L"Endfield.exe" ? 1 : 2); return true; };
    endfield::Decoy decoy;
    CHECK(endfield::StageDecoy(ops, fx.game, L"", true, decoy, nullptr) == DecoyError::signer_mismatch);
    CHECK(!Has(calls.log, "create_suspended") && fx.NothingStaged());
  }
  {  // a certificate that cannot be read counts as a mismatch
    StageFixture fx;
    Calls calls;
    auto ops = FakeOps(calls);
    ops.signer_hash = [&](const fs::path &, std::vector<uint8_t> &) { return false; };
    endfield::Decoy decoy;
    CHECK(endfield::StageDecoy(ops, fx.game, L"", true, decoy, nullptr) == DecoyError::signer_mismatch);
  }
  {  // the staged copy fails verification (tampered between the checks): the session folder is removed, nothing is started
    StageFixture fx;
    Calls calls;
    auto ops = FakeOps(calls);
    ops.verify_trust = [&](const fs::path &p) -> LONG { return p.filename() == L"Games.exe" ? static_cast<LONG>(0x80096010) : 0; };
    endfield::Decoy decoy;
    CHECK(endfield::StageDecoy(ops, fx.game, L"", true, decoy, nullptr) == DecoyError::stage_signer);
    CHECK(!Has(calls.log, "create_suspended") && fx.NothingStaged());
  }
  {  // the test switch skips every signature call
    StageFixture fx;
    Calls calls;
    const auto ops = FakeOps(calls);
    endfield::Decoy decoy;
    CHECK(endfield::StageDecoy(ops, fx.game, L"", false, decoy, nullptr) == DecoyError::ok);
    CHECK(!Has(calls.log, "verify"));
    endfield::ReleaseDecoy(ops, decoy);
  }
}

void TestStageCreateSuspendedFails() {
  StageFixture fx;
  Calls calls;
  auto ops = FakeOps(calls);
  ops.create_suspended = [&](const fs::path &, const fs::path &, PROCESS_INFORMATION *) -> BOOL {
    SetLastError(ERROR_ACCESS_DENIED);
    return FALSE;
  };
  endfield::Decoy decoy;
  DWORD os_error = 0;
  CHECK(endfield::StageDecoy(ops, fx.game, L"", true, decoy, &os_error) == DecoyError::create_suspended);
  CHECK(os_error == ERROR_ACCESS_DENIED);
  CHECK(decoy.process == nullptr);
  CHECK(fx.NothingStaged());  // the staged file does not stay behind
}

void TestReleaseOrder() {
  StageFixture fx;
  Calls calls;
  const auto ops = FakeOps(calls);
  endfield::Decoy decoy;
  CHECK(endfield::StageDecoy(ops, fx.game, L"", false, decoy, nullptr) == DecoyError::ok);
  calls.log.clear();
  endfield::ReleaseDecoy(ops, decoy);
  // terminate, then wait for the teardown (2 s), then close the handles, and only then the folder goes
  CHECK(calls.log.size() == 4);
  CHECK(calls.log.size() == 4 && calls.log[0] == "terminate:256" && calls.log[1] == "wait:256:2000" && calls.log[2] == "close:256" && calls.log[3] == "close:260");
  CHECK(decoy.process == nullptr && decoy.thread == nullptr && decoy.dir.empty());
  calls.log.clear();
  endfield::ReleaseDecoy(ops, decoy);  // a second release does nothing
  CHECK(calls.log.empty());
}

void TestSweepStale() {
  TempDir dir;
  const fs::path game = dir.path / L"Endfield.exe";
  const fs::path root = dir.path / L"_Hi3_Hg" / L"parent";
  WriteFile(root / L"2000000000" / L"aaaa" / L"Games.exe", "old");  // a pid that does not run
  WriteFile(root / std::to_wstring(GetCurrentProcessId()) / L"bbbb" / L"Games.exe", "mine");
  WriteFile(root / L"notapid" / L"cccc" / L"Games.exe", "other");
  endfield::SweepStale(dir.path);
  CHECK(!fs::exists(root / L"2000000000"));
  CHECK(fs::exists(root / std::to_wstring(GetCurrentProcessId()) / L"bbbb" / L"Games.exe"));
  CHECK(fs::exists(root / L"notapid" / L"cccc" / L"Games.exe"));
}

// ---- end to end: the real Win32 calls with the dummy game -------------------------------------------------------------

struct E2eFixture {
  TempDir dir;
  fs::path game = dir.path / L"Endfield.exe";
  fs::path source = dir.path / L"PlatformProcess.exe";
  fs::path record = dir.path / L"record.txt";
  E2eFixture(DWORD life_ms) {
    // FakeEndfield.exe (a GUI-subsystem program like the real game) sits beside this test program
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    const fs::path fake = fs::path(self).parent_path() / L"FakeEndfield.exe";
    fs::copy_file(fake, game);
    fs::copy_file(fake, source);
    SetEnvironmentVariableW(L"ECL_FAKE_RECORD", record.c_str());
    SetEnvironmentVariableW(L"ECL_FAKE_LIFE_MS", std::to_wstring(life_ms).c_str());
  }
  ~E2eFixture() {
    SetEnvironmentVariableW(L"ECL_FAKE_RECORD", nullptr);
    SetEnvironmentVariableW(L"ECL_FAKE_LIFE_MS", nullptr);
  }
};

constexpr uint32_t kTestFlags = ENDFIELD_FLAG_TEST_SKIP_SIGNER_CHECK | ENDFIELD_FLAG_TEST_LAUNCH_ONLY;

void TestEndToEndGameExit() {
  E2eFixture fx(1500);
  Job job;
  Session s = NewSession(fx.game, kTestFlags);
  CHECK(endfield_create_session(&s.request, &s.result, &s.handle) == ENDFIELD_OK);
  CHECK(endfield_attach_job(s.handle, job.handle, &s.result) == ENDFIELD_OK);
  CHECK(endfield_launch(s.handle, &s.result) == ENDFIELD_OK);
  EndfieldSessionInfo info{};
  info.size = sizeof(info);
  CHECK(endfield_get_session_info(s.handle, &info) == ENDFIELD_OK);
  CHECK(info.game_pid != 0 && info.decoy_pid != 0 && info.game_pid != info.decoy_pid);
  CHECK(info.parent_pid_seen == info.decoy_pid);  // the OS's view of the game's parent, taken right after CreateProcess
  const std::wstring staged = std::wstring(info.decoy_image, info.decoy_image_chars);
  CHECK(fs::exists(staged));
  CHECK(job.Contains(info.game_pid) && job.Contains(info.decoy_pid));  // both ended with the session job

  // what the game itself saw: its parent is Games.exe, in the staged session folder, and it is the decoy's pid
  CHECK(WaitFile(fx.record, 10000));
  if (!fs::exists(fx.record)) {
    DWORD code = 0;
    if (HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, info.game_pid)) { GetExitCodeProcess(h, &code); CloseHandle(h); }
    std::printf("    the dummy wrote no record; game pid %lu exit code 0x%08lx (259 = still running)\n", info.game_pid, code);
  }
  const std::wstring record = ReadWide(fx.record);
  CHECK(Field(record, L"self_pid") == std::to_wstring(info.game_pid));
  CHECK(Field(record, L"parent_pid") == std::to_wstring(info.decoy_pid));
  const std::wstring parent_image = Field(record, L"parent_image");
  CHECK(fs::path(parent_image).filename() == L"Games.exe");
  CHECK(Lower(parent_image).find(Lower((fx.dir.path / L"_Hi3_Hg" / L"parent").wstring())) == 0);
  CHECK(Lower(parent_image) == Lower(staged));
  CHECK(Field(record, L"in_job") == L"1");
  CHECK(Field(record, L"cmdline").find(L"-screen-width 1920 -screen-height 1080 -screen-refresh-rate 60 -screen-fullscreen 1") != std::wstring::npos);
  CHECK(Lower(Field(record, L"cwd")) == Lower(fx.dir.path.wstring()));
  CHECK(Alive(info.decoy_pid));  // suspended, never resumed: alive while the game runs

  // the game ends: the session ends the decoy and removes its folder
  CHECK(endfield_wait_game_exit(s.handle, 15000, &s.result) == ENDFIELD_OK);
  CHECK(WaitGone(info.decoy_pid, 3000));
  CHECK(!fs::exists(staged));
  CHECK(endfield_get_session_info(s.handle, &info) == ENDFIELD_OK && info.decoy_pid == 0);
  CHECK(endfield_release(s.handle) == ENDFIELD_OK);
}

void TestEndToEndJobClose() {
  E2eFixture fx(60000);
  Job job;
  Session s = NewSession(fx.game, kTestFlags);
  CHECK(endfield_create_session(&s.request, &s.result, &s.handle) == ENDFIELD_OK);
  CHECK(endfield_attach_job(s.handle, job.handle, &s.result) == ENDFIELD_OK);
  CHECK(endfield_launch(s.handle, &s.result) == ENDFIELD_OK);
  EndfieldSessionInfo info{};
  info.size = sizeof(info);
  CHECK(endfield_get_session_info(s.handle, &info) == ENDFIELD_OK);
  const DWORD game = info.game_pid, decoy = info.decoy_pid;
  const std::wstring staged = std::wstring(info.decoy_image, info.decoy_image_chars);
  CHECK(WaitFile(fx.record, 10000));
  CHECK(Alive(game) && Alive(decoy));
  job.Close();  // the session ends before Running: KILL_ON_JOB_CLOSE ends the game and the decoy together
  CHECK(WaitGone(game, 10000));
  CHECK(WaitGone(decoy, 10000));
  CHECK(endfield_release(s.handle) == ENDFIELD_OK);  // the game is gone: the release removes the folder
  CHECK(!fs::exists(staged));
}

void TestEndToEndSignerCheckFailsClosed() {
  E2eFixture fx(1000);
  // the dummy is not signed: with the check on (the production setting) nothing is started
  Session s = NewSession(fx.game, ENDFIELD_FLAG_TEST_LAUNCH_ONLY);
  CHECK(endfield_create_session(&s.request, &s.result, &s.handle) == ENDFIELD_OK);
  CHECK(endfield_launch(s.handle, &s.result) == ENDFIELD_ERR_DECOY_FAILED);
  CHECK(s.result.stage == ENDFIELD_STAGE_DECOY);
  CHECK(s.result.message != nullptr && s.result.message_chars > 0);
  Sleep(500);
  CHECK(!fs::exists(fx.record));  // no game process was created
  EndfieldSessionInfo info{};
  info.size = sizeof(info);
  CHECK(endfield_get_session_info(s.handle, &info) == ENDFIELD_OK && info.game_pid == 0 && info.decoy_pid == 0);
  std::error_code ec;
  bool leftover = false;
  for (const auto &e : fs::recursive_directory_iterator(fx.dir.path / L"_Hi3_Hg", ec))
    if (e.is_regular_file()) leftover = true;
  CHECK(!leftover);
  CHECK(endfield_release(s.handle) == ENDFIELD_OK);
}

void TestEndToEndSourceMissing() {
  E2eFixture fx(1000);
  fs::remove(fx.source);
  Session s = NewSession(fx.game, kTestFlags);
  CHECK(endfield_create_session(&s.request, &s.result, &s.handle) == ENDFIELD_OK);
  CHECK(endfield_launch(s.handle, &s.result) == ENDFIELD_ERR_DECOY_FAILED);
  CHECK(s.result.stage == ENDFIELD_STAGE_DECOY);
  CHECK(endfield_release(s.handle) == ENDFIELD_OK);
}

void TestEndToEndOldClientSize() {
  // a client built before `flags` existed sends ENDFIELD_REQUEST_SIZE_V1 bytes: it gets the defaults (decoy on, signatures required)
  E2eFixture fx(1000);
  Session s = NewSession(fx.game, kTestFlags, L"ignored.exe");
  s.request.size = ENDFIELD_REQUEST_SIZE_V1;
  CHECK(endfield_create_session(&s.request, &s.result, &s.handle) == ENDFIELD_OK);
  CHECK(endfield_launch(s.handle, &s.result) == ENDFIELD_ERR_DECOY_FAILED);  // unsigned dummy + signature check still on
  CHECK(endfield_release(s.handle) == ENDFIELD_OK);
  Session tooSmall = NewSession(fx.game, 0);
  tooSmall.request.size = ENDFIELD_REQUEST_SIZE_V1 - 1;
  CHECK(endfield_create_session(&tooSmall.request, &tooSmall.result, &tooSmall.handle) == ENDFIELD_ERR_ABI_MISMATCH);
}

// A real Authenticode-signed image (embedded signature) proves the production signature path, WinVerifyTrust and the signer comparison included.
fs::path FindSignedImage() {
  for (const wchar_t *candidate : {L"C:\\Program Files\\dotnet\\dotnet.exe", L"C:\\Program Files\\Git\\cmd\\git.exe", L"C:\\Windows\\System32\\OpenSSH\\ssh.exe"}) {
    std::vector<uint8_t> hash;
    if (fs::exists(candidate) && endfield::RealOps().verify_trust(candidate) == 0 && endfield::RealOps().signer_hash(candidate, hash)) return candidate;
  }
  return {};
}

void TestEndToEndRealSignature() {
  const fs::path signed_image = FindSignedImage();
  if (signed_image.empty()) {
    std::printf("    SKIP: no embedded-signature image found on this machine\n");
    return;
  }
  TempDir dir;
  const fs::path game = dir.path / L"Endfield.exe";
  fs::copy_file(signed_image, game);
  fs::copy_file(signed_image, dir.path / L"PlatformProcess.exe");
  // the unsigned copy of this test program as a different, invalid source
  wchar_t self[MAX_PATH];
  GetModuleFileNameW(nullptr, self, MAX_PATH);
  fs::copy_file(self, dir.path / L"Unsigned.exe");

  Calls ignored;
  endfield::Decoy decoy;
  const endfield::Ops real = endfield::RealOps();
  DWORD os_error = 0;
  CHECK(endfield::StageDecoy(real, game, L"", true, decoy, &os_error) == DecoyError::ok);  // same signer on both: accepted
  CHECK(decoy.pid != 0);
  endfield::ReleaseDecoy(real, decoy);
  endfield::Decoy rejected;
  CHECK(endfield::StageDecoy(real, game, L"Unsigned.exe", true, rejected, &os_error) == DecoyError::signer_source);
  CHECK(rejected.process == nullptr);
  endfield::Decoy bad_game;
  CHECK(endfield::StageDecoy(real, dir.path / L"Unsigned.exe", L"PlatformProcess.exe", true, bad_game, &os_error) == DecoyError::signer_game);
}

}  // namespace

int wmain(int argc, wchar_t **argv) {
  (void)argc;
  (void)argv;
  Run("unit: attribute setup (PARENT_PROCESS, extended startup info, list lifetime)", TestAttributeSetup);
  Run("unit: InitializeProcThreadAttributeList fails", TestInitFails);
  Run("unit: UpdateProcThreadAttribute fails", TestUpdateFails);
  Run("unit: CreateProcess fails (generic, 740)", TestCreateFails);
  Run("unit: the game's parent is not the decoy", TestParentMismatch);
  Run("unit: staging copies PlatformProcess.exe to the session folder as Games.exe", TestStageSuccess);
  Run("unit: staging with a missing or non-exe source", TestStageSourceMissing);
  Run("unit: staging a configured source (relative, absolute)", TestStageConfiguredSource);
  Run("unit: signature checks (game, source, signer mismatch, staged copy, test switch)", TestStageSignerChecks);
  Run("unit: the suspended decoy cannot be created", TestStageCreateSuspendedFails);
  Run("unit: release order and idempotence", TestReleaseOrder);
  Run("unit: stale session folders are swept, live ones kept", TestSweepStale);
  Run("e2e: dummy game sees Games.exe as parent; game exit ends the decoy", TestEndToEndGameExit);
  Run("e2e: closing the session job kills the game and the decoy", TestEndToEndJobClose);
  Run("e2e: an unsigned image fails closed before any process is created", TestEndToEndSignerCheckFailsClosed);
  Run("e2e: a missing decoy source fails with the decoy stage", TestEndToEndSourceMissing);
  Run("e2e: a request of the first ABI size gets the defaults", TestEndToEndOldClientSize);
  Run("e2e: a real signed image passes the production signature check; unsigned ones do not", TestEndToEndRealSignature);

  std::printf("%d tests, %d failed checks\n", g_run, g_failed);
  return g_failed ? 1 : 0;
}
