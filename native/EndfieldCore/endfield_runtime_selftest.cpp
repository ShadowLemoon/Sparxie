// Self-test of the Runtime's mechanics without a game: the pointer-mode hold,
// the ini keys and their defaults, the drag threshold, the device profile, the
// UI-size thunk of new panels and the cloud-mode slots (slot reader, the page
// the Runtime creates, the watch, uninstall, the ~10 s fallback). The Runtime's
// own source is included so its internals can be reached. Run:
// build\Release\EndfieldRuntimeSelfTest.exe (exit code 0 = all passed)
#include "endfield_runtime.cpp"

static int fails = 0;
#define CHECK(c)                                                               \
  do {                                                                         \
    if (!(c)) {                                                                \
      printf("FAIL line %d: %s\n", __LINE__, #c);                              \
      ++fails;                                                                 \
    }                                                                          \
  } while (0)

int main() {
  // the message hook corrects the module's field, leaves a reused object alone,
  // survives an unreadable address
  pm::S.field_off = 0x30;
  g_pm_want = 2;
  alignas(16) unsigned char module[0x80] = {};
  *reinterpret_cast<void **>(module) = &fails; // the "class" pointer
  pm::g_cached_module = module;
  pm::g_cached_class = &fails;
  *reinterpret_cast<int32_t *>(module + 0x30) = 1;
  pm::HoldFast();
  CHECK(*reinterpret_cast<int32_t *>(module + 0x30) == 2 &&
        pm::g_hold_fix == 1);
  pm::HoldFast();
  CHECK(pm::g_hold_fix == 1); // already right: no write
  *reinterpret_cast<void **>(module) =
      &pm::S; // the object was freed and reused: left alone
  *reinterpret_cast<int32_t *>(module + 0x30) = 1;
  pm::HoldFast();
  CHECK(*reinterpret_cast<int32_t *>(module + 0x30) == 1);
  unsigned char *gone = static_cast<unsigned char *>(
      VirtualAlloc(nullptr, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_NOACCESS));
  pm::g_cached_module = gone; // an unreadable address: dropped, no crash
  pm::HoldFast();
  CHECK(pm::g_cached_module == nullptr);
  g_pm_want = -1; // ui_pointers=off: nothing is written
  pm::g_cached_module = module;
  *reinterpret_cast<void **>(module) = &fails;
  pm::HoldFast();
  CHECK(*reinterpret_cast<int32_t *>(module + 0x30) == 1);

  // touch-made mouse messages are recognised by the signature in the extra
  // info, a real mouse is not
  CHECK(IsTouchMadeMouse(WM_LBUTTONDOWN, 0xFF5157C3ul) &&
        IsTouchMadeMouse(WM_MOUSEMOVE, 0xFF515700ul) &&
        IsTouchMadeMouse(WM_LBUTTONDBLCLK, 0xFF5157FFul));
  CHECK(!IsTouchMadeMouse(WM_LBUTTONDOWN, 0) &&
        !IsTouchMadeMouse(WM_LBUTTONDOWN, 0xFF5156FFul) &&
        !IsTouchMadeMouse(WM_RBUTTONDOWN, 0xFF5157C3ul) &&
        !IsTouchMadeMouse(WM_POINTERDOWN, 0xFF5157C3ul));

  // the ini keys
  wchar_t temp[MAX_PATH];
  GetTempPathW(MAX_PATH, temp);
  g_ini_path = std::wstring(temp) + L"endfield_runtime_selftest-" +
               std::to_wstring(GetCurrentProcessId()) + L".ini";
  auto write_ini = [](const char *text) {
    HANDLE f = CreateFileW(g_ini_path.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    DWORD w = 0;
    WriteFile(f, text, static_cast<DWORD>(strlen(text)), &w, nullptr);
    CloseHandle(f);
  };
  // no file: the shipped defaults (drag_px=auto, touch_mouse=drop,
  // ui_pointers=all, cloud_mode=on, ui_scale=auto, safe_area=auto, observe=1)
  DeleteFileW(g_ini_path.c_str());
  g_pm_want = 0;
  g_drop_touch_mouse = 0;
  ReadIni();
  CHECK(g_pm_want == 2 && g_drop_touch_mouse == 1 && g_drag_setting == "auto");
  CHECK(g_cloud_mode == 1 && g_ui_rel == 0.0f && !g_ui_off &&
        !g_spec_ini.Any() && g_flag_observe && g_ui_knob_mode == 2 &&
        !g_flag_notch_write && g_ini_notch_write);
  // the ini Sparxie ships writes the same values: the same state
  write_ini(
      "drag_px=auto\r\nlong_press_s=off\r\ntap_diag=off\r\ndrag_scan="
      "light\r\ntouch_mouse=drop\r\nclick_trace=off\r\nui_pointers="
      "all\r\npointer_guard=off\r\npointer_probe=off\r\n"
      "cloud_mode=on\r\nui_scale=auto\r\nsafe_area=auto\r\nobserve=1\r\n");
  ReadIni();
  CHECK(g_pm_want == 2 && g_drop_touch_mouse == 1 && g_drag_setting == "auto");
  CHECK(g_cloud_mode == 1 && g_ui_rel == 0.0f && !g_ui_off &&
        !g_spec_ini.Any() && g_flag_observe && g_ui_knob_mode == 2 &&
        !g_flag_notch_write);
  write_ini("ui_pointers=0\r\n  TOUCH_MOUSE=off\r\ndrag_px=30 \r\n");
  ReadIni();
  CHECK(g_pm_want == 0 && g_drop_touch_mouse == 0 && g_drag_setting == "30");
  write_ini(
      "ui_pointers=off\r\nui_pointers=garbage\r\n"); // the last line wins; text
                                                     // that is not a value
                                                     // means the default
  ReadIni();
  CHECK(g_pm_want == 2);
  write_ini("ui_pointers=off\r\n");
  ReadIni();
  CHECK(g_pm_want == -1);
  // cloud_mode=off: the PC path (standard knob off, notch through the game's
  // setting); ui_scale is clamped to 1.00 - 1.80; safe_area pixels
  write_ini(
      "cloud_mode=off\r\nui_scale=2.5\r\nsafe_area=120,100\r\nobserve=0\r\n");
  ReadIni();
  CHECK(g_cloud_mode == 0 && g_ui_knob_mode == 0 && g_flag_notch_write &&
        g_ui_rel == 1.8f && g_spec_ini.Any() && g_spec_ini.l_px == 120.0f &&
        g_spec_ini.r_px == 100.0f && !g_flag_observe);
  CHECK(g_notch_reeval &&
        g_force_profile); // safe_area changed: worked out again once
  write_ini("cloud_mode=off\r\nui_scale=0.5\r\nsafe_area=off\r\n");
  ReadIni();
  CHECK(g_ui_rel == 1.0f && !g_flag_notch_write && !g_ini_notch_write &&
        !g_spec_ini.Any());
  write_ini("ui_scale=off\r\nsafe_area=0,0\r\n");
  ReadIni();
  CHECK(g_ui_off && g_cloud_mode == 1 && g_spec_ini.Any() &&
        g_spec_ini.l_px == 0.0f);
  write_ini("ui_scale=big\r\n");
  ReadIni();
  CHECK(!g_ui_off && g_ui_rel == 0.0f);
  DeleteFileW(g_ini_path.c_str());
  ReadIni();

  // the drag threshold: 0.1 inch at the client's density, never below the
  // game's own value, an ini number, off
  std::string basis;
  CHECK(ComputeDragTarget("auto", 10, 2868, 1320, &basis) == 46);
  CHECK(ComputeDragTarget("auto", 10, 1320, 2868, &basis) ==
        46); // either orientation
  CHECK(ComputeDragTarget("auto", 10, 1920, 1080, &basis) ==
        16); // not in the table: 1.5 % of the height
  CHECK(ComputeDragTarget("auto", 60, 2868, 1320, &basis) ==
        60); // the game's value is larger: kept
  CHECK(ComputeDragTarget("30", 10, 2868, 1320, &basis) == 30);
  CHECK(ComputeDragTarget("off", 10, 2868, 1320, &basis) == 0);

  // the device profile: the stream size names the device; its insets in stream
  // pixels
  {
    float l = 0, r = 0, t = 0, b = 0;
    Profile p = BuildProfile(ProfileSpec{}, 2868, 1320);
    ComputeInsets(p, 2868, 1320, &l, &r, &t, &b);
    CHECK(p.name == "iphone-17-pro-max" && p.cls == "phone" &&
          p.ui_scale == 1.4f && static_cast<int>(l + 0.5f) == 186 &&
          static_cast<int>(r + 0.5f) == 186 && t == 0 && b == 0 &&
          ProfileHasInsets(p));
    p = BuildProfile(ProfileSpec{}, 2752, 2064);
    CHECK(p.name == "ipad-pro-13" && p.cls == "pad" && !ProfileHasInsets(p));
    p = BuildProfile(ProfileSpec{}, 1920, 1080);
    CHECK(p.name == "generic" && !ProfileHasInsets(p));
    p = BuildProfile(ProfileSpec{}, 2400, 1080);
    CHECK(p.name.rfind("android", 0) == 0);
    ProfileSpec px;
    px.given = true;
    px.l_px = 120;
    px.r_px = 100;
    p = BuildProfile(px, 2868, 1320);
    ComputeInsets(p, 2868, 1320, &l, &r, &t, &b);
    CHECK(p.source == "ini" && p.use_px && l == 120.0f && r == 100.0f &&
          ProfileHasInsets(p));
    ProfileSpec none;
    none.given = true;
    p = BuildProfile(none, 2868, 1320);
    CHECK(p.source == "ini" && !ProfileHasInsets(p));
  }

  // the lifecycle thunk of the UI size: puts the held size into a new helper
  // before its own Awake runs, calls the original, leaves other objects alone
  static int orig_calls = 0;
  struct Orig {
    static void __cdecl Fn(void *, const void *) { ++orig_calls; }
  };
  g_ui.helper_class = &fails;
  g_off_use = 0x20;
  g_off_res = 0x28;
  g_knob = kKnobStdDone;
  g_std_target[0] = 1422.0f;
  g_std_target[1] = 800.0f;
  g_life_orig[0] = &Orig::Fn;
  alignas(16) unsigned char helper[0x60] = {};
  *reinterpret_cast<void **>(helper) = &fails;
  g_life_thunks[0](helper, nullptr);
  CHECK(orig_calls == 1 && helper[0x20] == 1 &&
        *reinterpret_cast<float *>(helper + 0x28) == 1422.0f &&
        *reinterpret_cast<float *>(helper + 0x2C) == 800.0f &&
        g_life_primed == 1);
  alignas(16) unsigned char other[0x60] = {};
  *reinterpret_cast<void **>(other) = &pm::S;
  g_life_thunks[0](other, nullptr);
  CHECK(orig_calls == 2 && other[0x20] == 0 && g_life_primed == 1);
  g_knob = kKnobWaiting; // before the size is decided nothing is written
  g_life_thunks[0](helper, nullptr);
  CHECK(g_life_primed == 1);

  // cloud_mode: the hot-fix slot reader, the page the Runtime creates when the
  // game has none (allocation guard), the watch that fills the slots again when
  // the game replaces its page or directory or empties a slot, uninstall, and
  // the ~10 s fallback timer; on fake machine code and a fake slot directory
  // shaped like the game's
  {
    unsigned char *mem = static_cast<unsigned char *>(VirtualAlloc(
        nullptr, 0x20000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    auto new_page = [] {
      return static_cast<char *>(VirtualAlloc(
          nullptr, cl::kPageBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    };
    char *dir = reinterpret_cast<char *>(
        mem + 0x7000); // the page directory: 20 page pointers, +0x78 = page 15,
                       // all null (a session without a hot-fix)
    uintptr_t *global = reinterpret_cast<uintptr_t *>(mem + 0x6000);
    *global = reinterpret_cast<uintptr_t>(dir);
    void **entry = reinterpret_cast<void **>(dir + 0x78);
    auto build = [&](unsigned char *code, unsigned char *hot, int reg_rdx,
                     int fn_off) {
      int n = 0;
      code[n++] = 0x48;
      code[n++] = 0x83;
      code[n++] = 0xEC;
      code[n++] = 0x28; // sub rsp,0x28
      code[n++] = 0x48;
      code[n++] = 0x8B;
      code[n++] = 0x05; // mov rax,[rip+disp]
      const int32_t gd = static_cast<int32_t>(
          reinterpret_cast<unsigned char *>(global) - (code + n + 4));
      memcpy(code + n, &gd, 4);
      n += 4;
      code[n++] = 0x48;
      code[n++] = 0x8B;
      code[n++] = 0xD1; // mov rdx,rcx
      code[n++] = 0x48;
      code[n++] = 0x8B;
      code[n++] = reg_rdx ? 0x50 : 0x48;
      code[n++] = 0x78; // mov rdx|rcx,[rax+0x78]
      code[n++] = 0x48;
      code[n++] = 0x85;
      code[n++] = reg_rdx ? 0xD2 : 0xC9; // test
      code[n++] = 0x0F;
      code[n++] = 0x85; // jne hot
      const int32_t jd = static_cast<int32_t>(hot - (code + n + 4));
      memcpy(code + n, &jd, 4);
      int h = 0;
      hot[h++] = 0x48;
      hot[h++] = 0x8B;
      hot[h++] = reg_rdx ? 0x82 : 0x81;
      memcpy(hot + h, &fn_off, 4);
      h += 4; // mov rax,[reg+fn]
      hot[h++] = 0x48;
      hot[h++] = 0x85;
      hot[h++] = 0xC0;
      hot[h++] = 0x74;
      hot[h++] = 0x10;
      hot[h++] = 0x48;
      hot[h++] = 0x8B;
      hot[h++] = reg_rdx ? 0x92 : 0x89;
      const int ctx = fn_off + 8;
      memcpy(hot + h, &ctx, 4);
      h += 4; // mov reg,[reg+ctx]
    };
    unsigned char *code_user = mem + 0x100, *hot_user = mem + 0x1000,
                  *code_safe = mem + 0x200, *hot_safe = mem + 0x1100;
    build(code_user, hot_user, 0, 0x4300);
    build(code_safe, hot_safe, 1, 0x4430);
    struct MI {
      unsigned char *code;
    } mi_user{code_user}, mi_safe{code_safe};
    cl::HotSlot user, safe;
    CHECK(cl::ParseHotSlot(&mi_user, &user) && user.fn_off == 0x4300 &&
          user.ctx_off == 0x4308 && user.table_off == 0x78 &&
          user.global == reinterpret_cast<uintptr_t>(global));
    CHECK(cl::ParseHotSlot(&mi_safe, &safe) && safe.fn_off == 0x4430 &&
          safe.ctx_off == 0x4438);
    unsigned char junk[64] = {0x90, 0x90, 0x90};
    MI mi_junk{junk};
    cl::HotSlot none;
    CHECK(!cl::ParseHotSlot(&mi_junk, &none));
    CHECK(!cl::ParseHotSlot(nullptr, &none));

    // the test allocator stands in for il2cpp_alloc / il2cpp_free; it hands out
    // dirty memory (the Runtime must zero it) and can play the game's setter
    // racing in between
    static int allocs = 0, frees = 0;
    static void *last_alloc = nullptr, *last_free = nullptr;
    static void **race_entry = nullptr;
    static void *race_page = nullptr;
    struct TA {
      static void *__cdecl Alloc(size_t n) {
        ++allocs;
        void *p =
            VirtualAlloc(nullptr, n, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        memset(p, 0xAB, n);
        last_alloc = p;
        if (race_entry) { // the game's own setter stores its page while ours is
                          // being made
          *race_entry = race_page;
          race_entry = nullptr;
        }
        return p;
      }
      static void __cdecl Free(void *p) {
        ++frees;
        last_free = p;
      }
    };
    auto hook_u = reinterpret_cast<void *>(&cl::HookUserPlatform);
    auto hook_s = reinterpret_cast<void *>(&cl::HookSafeArea);
    auto at = [](void *page, int off) {
      return *reinterpret_cast<void **>(static_cast<char *>(page) + off);
    };

    // no page and no create: "not there", nothing allocated
    CHECK(!cl::ResolveSlots(user) && *entry == nullptr);
    // allocation guard 1: without the game's allocator no page is made
    cl::g_alloc = nullptr;
    cl::g_free = nullptr;
    CHECK(!cl::ResolveSlots(user, true) && *entry == nullptr && allocs == 0);
    cl::g_alloc = &TA::Alloc;
    cl::g_free = &TA::Free;
    // allocation guard 2: a slot outside the game's page size is refused before
    // anything is allocated
    cl::HotSlot big = user;
    big.fn_off = cl::kPageBytes;
    big.ctx_off = cl::kPageBytes + 8;
    CHECK(!cl::ResolveSlots(big, true) && *entry == nullptr && allocs == 0);

    // install into a session without a page: one zeroed page of the game's
    // size, published in the null entry, the two slots filled, everything else
    // zero
    cl::g_user = user;
    cl::g_safe = safe;
    g_pumps = 100;
    g_cloud_mode = 1;
    cl::Install();
    CHECK(cl::g_installed && g_cloud_active && allocs == 1 &&
          *entry == last_alloc && cl::g_our_page == last_alloc);
    void *ours = *entry;
    CHECK(at(ours, 0x4300) == hook_u && at(ours, 0x4430) == hook_s &&
          at(ours, 0x4308) == nullptr && at(ours, 0x4438) == nullptr);
    CHECK(at(ours, 0) == nullptr && at(ours, 0x10) == nullptr &&
          at(ours, 0x4310) == nullptr &&
          at(ours, cl::kPageBytes - 8) == nullptr);
    // an existing page is used as it is: no second allocation
    cl::Watch();
    CHECK(allocs == 1 && cl::g_reinstalls == 0);

    // hooks
    cl::g_user_platform = 8;
    CHECK(cl::HookUserPlatform(nullptr, nullptr) == 8);
    cl::g_user_platform = 11;
    CHECK(cl::HookUserPlatform(nullptr, nullptr) == 11);
    RectF rect{};
    g_have_profile =
        false; // no profile / no screen size: the full-screen rectangle (zeros
               // here), the return pointer is given back
    CHECK(cl::HookSafeArea(&rect, nullptr) == &rect);

    // re-install 1: the game puts in its own page (a hot-fix loaded)
    char *game_page = new_page();
    *entry = game_page;
    cl::Watch();
    CHECK(cl::g_installed && cl::g_reinstalls == 1 &&
          cl::g_user.page == game_page && at(game_page, 0x4300) == hook_u &&
          at(game_page, 0x4430) == hook_s && allocs == 1);
    // re-install 2: the game empties its slots (its "clear all")
    *reinterpret_cast<void **>(game_page + 0x4300) = nullptr;
    *reinterpret_cast<void **>(game_page + 0x4430) = nullptr;
    cl::Watch();
    CHECK(cl::g_reinstalls == 2 && at(game_page, 0x4300) == hook_u &&
          at(game_page, 0x4430) == hook_s);
    // re-install 3: a new directory without a page (metadata reloaded): a new
    // page is made in it
    char *dir2 = reinterpret_cast<char *>(mem + 0x7800);
    *global = reinterpret_cast<uintptr_t>(dir2);
    cl::Watch();
    void *ours2 = *reinterpret_cast<void **>(dir2 + 0x78);
    CHECK(cl::g_reinstalls == 3 && allocs == 2 && ours2 == last_alloc &&
          at(ours2, 0x4300) == hook_u && cl::g_user.dir == dir2);
    // re-install 4, allocation guard 3: the game's setter stores its page while
    // ours is made: the compare-exchange loses, ours is given back, the game's
    // page gets the slots
    char *dir3 = reinterpret_cast<char *>(mem + 0x7C00);
    char *game_page2 = new_page();
    race_entry = reinterpret_cast<void **>(dir3 + 0x78);
    race_page = game_page2;
    *global = reinterpret_cast<uintptr_t>(dir3);
    cl::Watch();
    CHECK(allocs == 3 && frees == 1 && last_free == last_alloc &&
          *reinterpret_cast<void **>(dir3 + 0x78) == game_page2 &&
          cl::g_our_page == ours2);
    CHECK(cl::g_installed && at(game_page2, 0x4300) == hook_u &&
          at(game_page2, 0x4430) == hook_s);

    // uninstall: the slots are empty again, the page stays in the directory
    cl::Uninstall();
    CHECK(!cl::g_installed && !g_cloud_active &&
          at(game_page2, 0x4300) == nullptr &&
          at(game_page2, 0x4430) == nullptr &&
          *reinterpret_cast<void **>(dir3 + 0x78) == game_page2);
    // a foreign function in a slot is kept and put back
    void *foreign = reinterpret_cast<void *>(&TA::Free);
    *reinterpret_cast<void **>(game_page2 + 0x4300) = foreign;
    cl::Install();
    CHECK(cl::g_installed && at(game_page2, 0x4300) == hook_u &&
          cl::g_user.old_fn == foreign);
    cl::Uninstall();
    CHECK(at(game_page2, 0x4300) == foreign);
    *reinterpret_cast<void **>(game_page2 + 0x4300) = nullptr;
    // the directory is gone (the game's shutdown): nothing more is written, no
    // crash
    cl::Install();
    CHECK(cl::g_installed);
    *global = 0;
    cl::Watch();
    CHECK(!cl::g_installed && !g_cloud_active && cl::g_user.fn_slot == nullptr);
    cl::Uninstall(); // not installed: no write
    CHECK(at(game_page2, 0x4300) ==
          hook_u); // the Runtime did not touch the old page after the directory
                   // went away

    // fallback timer: cloud_mode on, the slots cannot be filled: the PC path
    // after ~10 s (40 pumps) of the first attempt, never earlier; it stays
    static SharedState fake_shared{};
    SharedState *saved_shared = g_shared;
    g_shared = &fake_shared;
    g_have_profile = true;
    cl::g_parsed = true;
    cl::g_user.found = false; // Prepare() fails
    g_cloud_fallback = false;
    cl::g_try_since = -1;
    g_ui_knob_mode = 2;
    g_ini_notch_write = true;
    g_ui_knob_mode = 2;
    g_flag_notch_write = false;
    for (g_pumps = 200; g_pumps < 242; ++g_pumps) {
      cl::Tick();
      if (g_pumps == 203)
        CHECK(cl::g_try_since == 203 && !g_cloud_fallback);
    }
    CHECK(
        !g_cloud_fallback && g_ui_knob_mode == 2 &&
        !g_flag_notch_write); // 39 pumps after the first attempt: still waiting
    for (; g_pumps < 250; ++g_pumps)
      cl::Tick();
    CHECK(g_cloud_fallback && g_ui_knob_mode == 0 && g_flag_notch_write &&
          !cl::g_installed);
    // sticky: even when the slots become fillable, the session stays on the PC
    // path (no second size jump)
    *global = reinterpret_cast<uintptr_t>(dir3);
    cl::g_user = user;
    cl::g_safe = safe;
    for (; g_pumps < 270; ++g_pumps)
      cl::Tick();
    CHECK(g_cloud_fallback && !cl::g_installed);
    // no fallback when the slots are filled in time
    g_cloud_fallback = false;
    cl::g_try_since = -1;
    g_ui_knob_mode = 2;
    g_flag_notch_write = false;
    for (g_pumps = 300; g_pumps < 400; ++g_pumps)
      cl::Tick();
    CHECK(cl::g_installed && !g_cloud_fallback && g_ui_knob_mode == 2 &&
          at(game_page2, 0x4300) == hook_u);
    // cloud_mode off: slots emptied by the next tick, the timer is reset
    g_cloud_mode = 0;
    cl::Tick();
    CHECK(!cl::g_installed && at(game_page2, 0x4300) == nullptr &&
          cl::g_try_since == -1);
    g_shared = saved_shared;
    g_have_profile = false;
  }

  DeleteFileW(g_ini_path.c_str());
  printf("endfield_runtime_selftest: %s (%d failed check(s))\n",
         fails ? "FAILED" : "ok", fails);
  return fails ? 1 : 0;
}
