// See endfield_decoy.h. Method: Hi3Helper.Plugin.Endfield / Hi3Helper.Hypergryph.Core (MIT, Collapse Launcher),
// Management/GameDecoyParentBridge.cs and Management/ParentProcessBridge.cs at commit 1efd304; ported, not copied.
#include "endfield_decoy.h"

#include <Rpc.h>
#include <Softpub.h>
#include <bcrypt.h>
#include <wincrypt.h>
#include <wintrust.h>

#include <algorithm>
#include <cwctype>
#include <system_error>

namespace fs = std::filesystem;

namespace endfield {
namespace {

constexpr wchar_t kStageRoot[] = L"_Hi3_Hg";  // the plugin's PluginPaths.Root, so a Collapse install and this share one folder
constexpr wchar_t kStageName[] = L"parent";

struct PebInfo {  // PROCESS_BASIC_INFORMATION
  LONG_PTR exit_status;
  PVOID peb;
  ULONG_PTR affinity;
  LONG_PTR base_priority;
  ULONG_PTR unique_pid;
  ULONG_PTR parent_pid;
};

std::wstring Lower(std::wstring text) {
  std::transform(text.begin(), text.end(), text.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
  return text;
}

std::wstring NewGuidText() {
  UUID id{};
  UuidCreate(&id);
  wchar_t text[40] = {};
  swprintf_s(text, L"%08lx%04x%04x%02x%02x%02x%02x%02x%02x%02x%02x", id.Data1, id.Data2, id.Data3, id.Data4[0], id.Data4[1], id.Data4[2],
             id.Data4[3], id.Data4[4], id.Data4[5], id.Data4[6], id.Data4[7]);
  return text;
}

LONG RealVerifyTrust(const fs::path &file) {
  const std::wstring native = file.wstring();
  WINTRUST_FILE_INFO file_info{};
  file_info.cbStruct = sizeof(file_info);
  file_info.pcwszFilePath = native.c_str();
  WINTRUST_DATA data{};
  data.cbStruct = sizeof(data);
  data.dwUIChoice = WTD_UI_NONE;
  data.fdwRevocationChecks = WTD_REVOKE_NONE;
  data.dwUnionChoice = WTD_CHOICE_FILE;
  data.pFile = &file_info;
  data.dwStateAction = WTD_STATEACTION_VERIFY;
  data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;  // 0x100, as the plugin: no network
  GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
  const LONG status = WinVerifyTrust(nullptr, &action, &data);
  data.dwStateAction = WTD_STATEACTION_CLOSE;
  WinVerifyTrust(nullptr, &action, &data);
  return status;
}

// The signer certificate of the first embedded signature (what .NET's X509Certificate.CreateFromSignedFile reads).
bool RealSignerHash(const fs::path &file, std::vector<uint8_t> &hash) {
  HCERTSTORE store = nullptr;
  HCRYPTMSG message = nullptr;
  const std::wstring native = file.wstring();
  if (!CryptQueryObject(CERT_QUERY_OBJECT_FILE, native.c_str(), CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED, CERT_QUERY_FORMAT_FLAG_BINARY, 0,
                        nullptr, nullptr, nullptr, &store, &message, nullptr))
    return false;
  bool ok = false;
  DWORD size = 0;
  if (CryptMsgGetParam(message, CMSG_SIGNER_INFO_PARAM, 0, nullptr, &size)) {
    std::vector<uint8_t> raw(size);
    if (CryptMsgGetParam(message, CMSG_SIGNER_INFO_PARAM, 0, raw.data(), &size)) {
      const auto *signer = reinterpret_cast<const CMSG_SIGNER_INFO *>(raw.data());
      CERT_INFO wanted{};
      wanted.Issuer = signer->Issuer;
      wanted.SerialNumber = signer->SerialNumber;
      if (PCCERT_CONTEXT cert = CertFindCertificateInStore(store, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, 0, CERT_FIND_SUBJECT_CERT, &wanted, nullptr)) {
        DWORD length = 32;
        hash.assign(length, 0);
        ok = CryptHashCertificate2(BCRYPT_SHA256_ALGORITHM, 0, nullptr, cert->pbCertEncoded, cert->cbCertEncoded, hash.data(), &length) != 0;
        CertFreeCertificateContext(cert);
      }
    }
  }
  if (message) CryptMsgClose(message);
  if (store) CertCloseStore(store, 0);
  return ok;
}

DWORD RealParentPid(HANDLE process) {
  using Query = LONG(WINAPI *)(HANDLE, ULONG, PVOID, ULONG, PULONG);
  const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
  const auto query = ntdll ? reinterpret_cast<Query>(GetProcAddress(ntdll, "NtQueryInformationProcess")) : nullptr;
  PebInfo info{};
  ULONG returned = 0;
  if (!query || query(process, 0 /* ProcessBasicInformation */, &info, sizeof(info), &returned) != 0) return 0;
  return static_cast<DWORD>(info.parent_pid);
}

BOOL RealCreateSuspended(const fs::path &exe, const fs::path &cwd, PROCESS_INFORMATION *info) {
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  const std::wstring app = exe.wstring();
  const std::wstring dir = cwd.wstring();
  return CreateProcessW(app.c_str(), nullptr, nullptr, nullptr, FALSE, CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, dir.c_str(), &startup, info);
}

// "Does the image verify and does it carry the game's signer certificate" (plugin: HasMatchingSigner).
DecoyError CheckSigners(const Ops &ops, const fs::path &game, const fs::path &image, bool staged) {
  if (ops.verify_trust(game) != 0) return DecoyError::signer_game;
  if (ops.verify_trust(image) != 0) return staged ? DecoyError::stage_signer : DecoyError::signer_source;
  std::vector<uint8_t> a, b;
  if (!ops.signer_hash(image, a) || !ops.signer_hash(game, b) || a.empty() || a != b) return staged ? DecoyError::stage_signer : DecoyError::signer_mismatch;
  return DecoyError::ok;
}

void RemoveTree(const fs::path &dir) {
  std::error_code ec;
  fs::remove_all(dir, ec);
}

}  // namespace

Ops RealOps() {
  Ops ops;
  ops.verify_trust = RealVerifyTrust;
  ops.signer_hash = RealSignerHash;
  ops.create_suspended = RealCreateSuspended;
  ops.init_attr = [](LPPROC_THREAD_ATTRIBUTE_LIST list, DWORD count, DWORD flags, PSIZE_T size) { return InitializeProcThreadAttributeList(list, count, flags, size); };
  ops.update_attr = [](LPPROC_THREAD_ATTRIBUTE_LIST list, DWORD flags, DWORD_PTR attribute, PVOID value, SIZE_T size, PVOID previous, PSIZE_T returned) {
    return UpdateProcThreadAttribute(list, flags, attribute, value, size, previous, returned);
  };
  ops.delete_attr = [](LPPROC_THREAD_ATTRIBUTE_LIST list) { DeleteProcThreadAttributeList(list); };
  ops.create_process = [](LPCWSTR app, LPWSTR cmd, BOOL inherit, DWORD flags, LPCWSTR cwd, STARTUPINFOEXW *startup, PROCESS_INFORMATION *info) {
    return CreateProcessW(app, cmd, nullptr, nullptr, inherit, flags, nullptr, cwd, &startup->StartupInfo, info);
  };
  ops.parent_pid_of = RealParentPid;
  ops.terminate = [](HANDLE process) { TerminateProcess(process, 0); };
  ops.wait = [](HANDLE handle, DWORD ms) { WaitForSingleObject(handle, ms); };
  ops.close = [](HANDLE handle) { CloseHandle(handle); };
  return ops;
}

DecoyError StageDecoy(const Ops &ops, const fs::path &game_exe, const std::wstring &configured_source, bool verify_signer, Decoy &out, DWORD *os_error) {
  auto fail = [&](DecoyError error) {
    if (os_error) *os_error = GetLastError();
    return error;
  };
  std::error_code ec;
  const fs::path game_dir = fs::absolute(game_exe, ec).parent_path();
  // "PlatformProcess.exe" unless one was configured; a relative path is relative to the game folder (plugin: Path.GetFullPath(source, gameDirectory))
  const fs::path wanted = configured_source.empty() ? fs::path(kDefaultDecoySource) : fs::path(configured_source);
  const fs::path source = wanted.is_absolute() ? wanted : game_dir / wanted;
  if (Lower(source.extension().wstring()) != L".exe" || !fs::is_regular_file(source, ec)) {
    SetLastError(ERROR_FILE_NOT_FOUND);
    return fail(DecoyError::source_missing);
  }
  if (verify_signer) {
    if (const DecoyError error = CheckSigners(ops, game_exe, source, false); error != DecoyError::ok) return fail(error);
  }

  const fs::path dir = game_dir / kStageRoot / kStageName / std::to_wstring(GetCurrentProcessId()) / NewGuidText();
  fs::create_directories(dir, ec);
  if (ec) {
    SetLastError(static_cast<DWORD>(ec.value()));
    return fail(DecoyError::stage_dir);
  }
  const fs::path staged = dir / kDecoyImageName;
  fs::copy_file(source, staged, fs::copy_options::none, ec);
  if (ec) {
    SetLastError(static_cast<DWORD>(ec.value()));
    const DecoyError error = fail(DecoyError::stage_copy);
    RemoveTree(dir);
    return error;
  }
  SetFileAttributesW(staged.c_str(), FILE_ATTRIBUTE_NORMAL);
  if (verify_signer) {
    if (const DecoyError error = CheckSigners(ops, game_exe, staged, true); error != DecoyError::ok) {
      const DecoyError reported = fail(error);
      RemoveTree(dir);
      return reported;
    }
  }

  PROCESS_INFORMATION info{};
  if (!ops.create_suspended(staged, dir, &info)) {
    const DecoyError error = fail(DecoyError::create_suspended);
    RemoveTree(dir);
    return error;
  }
  out.process = info.hProcess;
  out.thread = info.hThread;
  out.pid = info.dwProcessId;
  out.staged = staged;
  out.dir = dir;
  return DecoyError::ok;
}

DecoyError StartWithParent(const Ops &ops, const Decoy &decoy, const fs::path &game_exe, std::wstring command_line, DWORD extra_flags, PROCESS_INFORMATION &info,
                           DWORD *os_error, DWORD *parent_seen) {
  auto fail = [&](DecoyError error) {
    if (os_error) *os_error = GetLastError();
    return error;
  };
  SIZE_T size = 0;
  ops.init_attr(nullptr, 1, 0, &size);  // the sizing call fails by design (ERROR_INSUFFICIENT_BUFFER)
  std::vector<uint8_t> storage(size ? size : 1);
  auto *list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
  if (!ops.init_attr(list, 1, 0, &size)) return fail(DecoyError::attr_init);

  HANDLE parent = decoy.process;
  if (!ops.update_attr(list, 0, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, &parent, sizeof(parent), nullptr, nullptr)) {
    const DecoyError error = fail(DecoyError::attr_update);
    ops.delete_attr(list);
    return error;
  }

  STARTUPINFOEXW startup{};
  startup.StartupInfo.cb = sizeof(startup);
  startup.lpAttributeList = list;
  const std::wstring app = game_exe.wstring();
  const std::wstring cwd = game_exe.parent_path().wstring();
  command_line.push_back(L'\0');  // CreateProcessW may write into the buffer
  const BOOL created = ops.create_process(app.c_str(), command_line.data(), FALSE, EXTENDED_STARTUPINFO_PRESENT | extra_flags, cwd.c_str(), &startup, &info);
  const DWORD last = GetLastError();
  ops.delete_attr(list);
  if (!created) {
    SetLastError(last);
    return fail(last == ERROR_ELEVATION_REQUIRED ? DecoyError::create_game_elevation : DecoyError::create_game);
  }

  const DWORD seen = ops.parent_pid_of(info.hProcess);
  if (parent_seen) *parent_seen = seen;
  if (seen != decoy.pid) {
    ops.terminate(info.hProcess);
    ops.wait(info.hProcess, 2000);
    ops.close(info.hThread);
    ops.close(info.hProcess);
    info = {};
    SetLastError(ERROR_INVALID_DATA);
    return fail(DecoyError::parent_mismatch);
  }
  return DecoyError::ok;
}

void ReleaseDecoy(const Ops &ops, Decoy &decoy) {
  if (decoy.process) {
    ops.terminate(decoy.process);
    // TerminateProcess only starts the teardown; the image file cannot be removed before the mapping is gone
    ops.wait(decoy.process, 2000);
    ops.close(decoy.process);
    decoy.process = nullptr;
  }
  if (decoy.thread) {
    ops.close(decoy.thread);
    decoy.thread = nullptr;
  }
  if (!decoy.dir.empty()) {
    RemoveTree(decoy.dir);
    std::error_code ec;
    fs::remove(decoy.dir.parent_path(), ec);  // the owner pid folder, when it is empty now
    decoy.dir.clear();
  }
}

void SweepStale(const fs::path &game_dir) {
  std::error_code ec;
  const fs::path root = game_dir / kStageRoot / kStageName;
  for (fs::directory_iterator owner(root, ec), end; !ec && owner != end; owner.increment(ec)) {
    if (!owner->is_directory(ec)) continue;
    wchar_t *stop = nullptr;
    const DWORD pid = static_cast<DWORD>(std::wcstoul(owner->path().filename().c_str(), &stop, 10));
    if (!pid || (stop && *stop)) continue;
    if (pid == GetCurrentProcessId()) continue;
    if (HANDLE alive = OpenProcess(SYNCHRONIZE, FALSE, pid)) {
      const bool running = WaitForSingleObject(alive, 0) == WAIT_TIMEOUT;
      CloseHandle(alive);
      if (running) continue;  // another session (or Collapse) is still using it
    }
    RemoveTree(owner->path());
  }
}

}  // namespace endfield
