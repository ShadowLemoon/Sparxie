// The decoy-parent launch method for Endfield.exe.
//
// The method is the one of the Collapse Launcher Endfield plugin (Hi3Helper.Plugin.Endfield,
// MIT, Copyright (c) 2025 Collapse Launcher; see THIRD-PARTY-NOTICES.md): a Hypergryph-signed
// image is copied to a session folder as "Games.exe", started suspended and never resumed, and
// handed to CreateProcess as PROC_THREAD_ATTRIBUTE_PARENT_PROCESS of the game, so that the
// client's check of its immediate parent finds the file it expects. Ported to C++ here (the
// plugin is C#); the Win32 calls sit behind Ops so the attribute setup and every failure path are
// unit-tested without a game.
#pragma once

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

namespace endfield {

// The file name the client's parent-process check accepts (plugin: ParentProcessBridge.GamesExecutableName).
inline constexpr wchar_t kDecoyImageName[] = L"Games.exe";
// The default source image, resolved against the game folder (plugin: GameDecoyParentBridge.Create).
inline constexpr wchar_t kDefaultDecoySource[] = L"PlatformProcess.exe";

enum class DecoyError {
  ok,
  source_missing,      // the source is not an existing .exe
  signer_game,         // WinVerifyTrust rejects the game image
  signer_source,       // WinVerifyTrust rejects the source image
  signer_mismatch,     // the two signer certificates differ (or cannot be read)
  stage_dir,           // the session folder cannot be created
  stage_copy,          // the source cannot be copied there
  stage_signer,        // the staged copy fails verification
  create_suspended,    // the suspended decoy process cannot be created
  attr_init,           // InitializeProcThreadAttributeList failed
  attr_update,         // UpdateProcThreadAttribute failed
  create_game,         // CreateProcessW for the game failed
  create_game_elevation,  // ... with ERROR_ELEVATION_REQUIRED (740): the caller is not elevated
  parent_mismatch,     // the created game's parent is not the decoy
};

// The Win32 seams. RealOps() is the production table; the tests replace single members.
struct Ops {
  // WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2) on one file: 0 = valid signature.
  std::function<LONG(const std::filesystem::path &)> verify_trust;
  // SHA-256 of the signer certificate of one file's embedded signature.
  std::function<bool(const std::filesystem::path &, std::vector<uint8_t> &)> signer_hash;
  // CreateProcessW(CREATE_SUSPENDED | CREATE_NO_WINDOW) for the decoy.
  std::function<BOOL(const std::filesystem::path &exe, const std::filesystem::path &cwd, PROCESS_INFORMATION *)> create_suspended;
  std::function<BOOL(LPPROC_THREAD_ATTRIBUTE_LIST, DWORD, DWORD, PSIZE_T)> init_attr;
  std::function<BOOL(LPPROC_THREAD_ATTRIBUTE_LIST, DWORD, DWORD_PTR, PVOID, SIZE_T, PVOID, PSIZE_T)> update_attr;
  std::function<void(LPPROC_THREAD_ATTRIBUTE_LIST)> delete_attr;
  std::function<BOOL(LPCWSTR app, LPWSTR cmd, BOOL inherit, DWORD flags, LPCWSTR cwd, STARTUPINFOEXW *, PROCESS_INFORMATION *)> create_process;
  // InheritedFromUniqueProcessId of a process (0 when unknown).
  std::function<DWORD(HANDLE)> parent_pid_of;
  std::function<void(HANDLE)> terminate;
  std::function<void(HANDLE, DWORD ms)> wait;
  std::function<void(HANDLE)> close;
};
Ops RealOps();

// One staged, suspended decoy process.
struct Decoy {
  HANDLE process = nullptr;
  HANDLE thread = nullptr;
  DWORD pid = 0;
  std::filesystem::path staged;   // <game dir>\_Hi3_Hg\parent\<owner pid>\<guid>\Games.exe
  std::filesystem::path dir;      // the session folder
};

// Checks the source (game folder's PlatformProcess.exe when `configured_source` is empty), requires valid
// signatures on both images with the same signer certificate (unless !verify_signer, test-only), copies it to
// a fresh session folder as Games.exe, verifies the copy again and starts it suspended. `os_error` receives
// GetLastError() of the failing call.
DecoyError StageDecoy(const Ops &ops, const std::filesystem::path &game_exe, const std::wstring &configured_source,
                      bool verify_signer, Decoy &out, DWORD *os_error);

// CreateProcessW(game_exe, command_line) with the decoy as its parent: STARTUPINFOEX + one attribute
// (PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = the decoy's process handle), EXTENDED_STARTUPINFO_PRESENT | extra_flags,
// no handle inheritance, the game's folder as working directory; then checks the new process' parent is the decoy.
// On parent_mismatch the new process is terminated and closed.
DecoyError StartWithParent(const Ops &ops, const Decoy &decoy, const std::filesystem::path &game_exe, std::wstring command_line,
                           DWORD extra_flags, PROCESS_INFORMATION &info, DWORD *os_error, DWORD *parent_seen);

// Terminates the decoy, waits for its teardown (2 s), closes the handles and removes the session folder.
void ReleaseDecoy(const Ops &ops, Decoy &decoy);

// Removes session folders whose owning pid no longer runs (a crashed session); locked ones stay.
void SweepStale(const std::filesystem::path &game_dir);

}  // namespace endfield
