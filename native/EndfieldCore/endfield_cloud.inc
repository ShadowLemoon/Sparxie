// cloud_mode (included into endfield_runtime.cpp, inside its anonymous namespace, before Pump()).
//
// What the game's own cloud-gaming design does (found in the game's metadata and code, see CLOUD_MODE.md): the game separates the DEVICE platform (the PC it runs on) from the USER platform (the phone
// that plays). Beyond.DeviceInfo.get_userPlatform returns CloudGame.platform when Application.isCloudGame is true and the device platform otherwise. Every UI decision goes through it: UI scale
// (UIConst.GetResolutionScale: isPCorConsoleUser -> 1.25, otherwise the phone value), mobile UI layers (PlatformLayer.GetUserPlatformLayer), the notch padding (DeviceInfoManager.NotchPadding). The device
// safe area comes from DeviceInfo.safeArea (the provider's Screen.safeArea on a PC, CloudGame.safeArea in cloud mode).
// Application.isCloudGame itself is gated by a key the engine checks (cloudkey), and in cloud mode login channel, text input and reconnect go through the cloud bridge: not used here.
//
// cloud_mode=on takes the same two inputs through the game's own hot-fix extension points instead: the methods DeviceInfo.get_userPlatform and DeviceInfo.get_safeArea begin with a check of a table of
// function-pointer slots (the game's iFix hot-patch table). When a slot holds a function, the method calls it instead of its own body. The Runtime fills exactly these two slots (data only, no code is
// patched) with: the user platform of the stream's client device (iOS 8 / Android 11) and the client's safe area rectangle. Everything else the game computes from them (UI scale, mobile layers,
// notch padding) is the game's own code. The slot offsets are read from the machine code of the two methods (checked byte patterns), not hard-coded.
//
// The slot table (read from the game's code, see CLOUD_MODE.md "slot table"): a native page directory in a global; every method reads `page = dir[k]; if (page && page[i].fn) return
// page[i].fn(page[i].ctx, ...)`. If the game puts in a new page or directory later, the two slots are filled again in the new one. Turning cloud_mode off empties the two slots.
// If the slots cannot be filled within ~10 s of the first attempt, the Runtime falls back to the PC path (the UI size knob and the notch setting, as with cloud_mode=off) for the session.
//
// endfield-ui.ini:  cloud_mode=on (default) | off (changed live: off puts the slots back to empty).

struct RectF { float x, y, w, h; };

namespace cl {

volatile LONG g_user_platform = 8;  // RuntimePlatform: 8 iPhonePlayer, 11 Android
volatile LONG g_user_calls = 0, g_safe_calls = 0;
bool g_installed = false;
LONG g_installed_at = 0, g_verify_due = 0, g_verify_count = 0;
LONG g_try_since = -1;      // pump of the first install attempt with cloud_mode on (the fallback timer runs from here)
LONG g_reinstalls = 0;      // how often the slots were filled again (the game replaced its page/directory or emptied a slot)
constexpr LONG kFallbackPumps = 40;   // ~10 s (250 ms pumps)

const char *g_last_fail = "not tried yet";

struct HotSlot {
  const char *what = "";
  uintptr_t global = 0;     // address of the global that holds the page directory; the directory's +table_off entry is the page with the slots
  int table_off = -1;
  int fn_off = -1, ctx_off = -1;
  bool found = false;
  void **fn_slot = nullptr, **ctx_slot = nullptr;
  void *old_fn = nullptr;
  void *dir = nullptr, *page = nullptr;  // what the slots were filled in (for the watch)
};
HotSlot g_user, g_safe;

int __fastcall HookUserPlatform(void * /*ctx*/, void * /*arg*/) {
  const LONG n = InterlockedIncrement(&g_user_calls);
  if (n == 1)
    Log("cloud: the game asked for the user platform (first call): answered %ld", g_user_platform);
  return static_cast<int>(g_user_platform);
}

RectF CurrentRect(int *w_out = nullptr, int *h_out = nullptr) {
  int w = g_screen_width ? g_screen_width() : 0, h = g_screen_height ? g_screen_height() : 0;
  float l = 0, r = 0, t = 0, b = 0;
  if (g_have_profile)
    ComputeInsets(g_profile, w, h, &l, &r, &t, &b);
  RectF rect{l, b, static_cast<float>(w) - l - r, static_cast<float>(h) - t - b};
  if (w <= 0 || h <= 0 || rect.w <= 0 || rect.h <= 0)
    rect = {0, 0, static_cast<float>(w), static_cast<float>(h)};
  if (w_out) *w_out = w;
  if (h_out) *h_out = h;
  return rect;
}

RectF *__fastcall HookSafeArea(RectF *ret, void * /*ctx*/) {
  int w = 0, h = 0;
  const RectF rect = CurrentRect(&w, &h);
  if (ret)
    *ret = rect;
  const LONG n = InterlockedIncrement(&g_safe_calls);
  static RectF last = {-1, -1, -1, -1};
  if (n <= 3 || rect.x != last.x || rect.w != last.w || rect.y != last.y || rect.h != last.h) {
    if (n <= 30)
      Log("cloud: the game read the device safe area (call %ld): Rect(x %.0f, y %.0f, w %.0f, h %.0f) on %d x %d", n, rect.x, rect.y, rect.w, rect.h, w, h);
    last = rect;
  }
  return ret;
}

// Reads the hot-fix check at the start of a managed method: mov rax,[rip+G]; ...; mov rcx|rdx,[rax+T]; test; jne HOT; and at HOT: mov rax,[rcx|rdx+fn]; test rax,rax; je; ...; mov rcx|rdx,[..+ctx].
bool ParseHotSlot(const void *method, HotSlot *out) {
  const unsigned char *code = method ? *reinterpret_cast<unsigned char *const *>(method) : nullptr;
  if (!code)
    return false;
  __try {
    int g_at = -1;
    for (int i = 0; i < 40 && g_at < 0; ++i)
      if (code[i] == 0x48 && code[i + 1] == 0x8B && code[i + 2] == 0x05)
        g_at = i;
    if (g_at < 0)
      return false;
    int32_t disp;
    memcpy(&disp, code + g_at + 3, 4);
    out->global = reinterpret_cast<uintptr_t>(code + g_at + 7 + disp);
    int t_at = -1, reg = 0;
    for (int i = g_at + 7; i < g_at + 40 && t_at < 0; ++i)
      if (code[i] == 0x48 && code[i + 1] == 0x8B && (code[i + 2] == 0x48 || code[i + 2] == 0x50) && code[i + 3] < 0x80) {  // mov rcx|rdx,[rax+disp8]
        t_at = i;
        reg = code[i + 2] == 0x48 ? 1 : 2;
        out->table_off = code[i + 3];
      }
    if (t_at < 0)
      return false;
    int j_at = -1;
    for (int i = t_at + 4; i < t_at + 24 && j_at < 0; ++i)
      if (code[i] == 0x0F && code[i + 1] == 0x85)
        j_at = i;
    if (j_at < 0)
      return false;
    memcpy(&disp, code + j_at + 2, 4);
    const unsigned char *hot = code + j_at + 6 + disp;
    const unsigned char want_modrm = reg == 1 ? 0x81 : 0x82;  // mov rax,[rcx|rdx+disp32]
    if (!(hot[0] == 0x48 && hot[1] == 0x8B && hot[2] == want_modrm))
      return false;
    memcpy(&out->fn_off, hot + 3, 4);
    int c_at = -1;
    for (int i = 7; i < 40 && c_at < 0; ++i)
      if (hot[i] == 0x48 && hot[i + 1] == 0x8B && (hot[i + 2] == 0x89 || hot[i + 2] == 0x92)) {  // mov rcx|rdx,[rcx|rdx+disp32]
        c_at = i;
        memcpy(&out->ctx_off, hot + i + 3, 4);
      }
    out->found = c_at >= 0 && out->fn_off > 0 && out->fn_off < 0x100000 && out->ctx_off == out->fn_off + 8;
    return out->found;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}

// Reads directory and page as they are now (no writes). false when the directory is not there or not readable.
bool ReadDirPage(const HotSlot &s, void **dir_out, void ***page_at_out, void **page_out) {
  *dir_out = nullptr;
  *page_at_out = nullptr;
  *page_out = nullptr;
  if (!s.found)
    return false;
  __try {
    void *dir = *reinterpret_cast<void *volatile *>(s.global);
    if (!dir)
      return false;
    void **page_at = reinterpret_cast<void **>(static_cast<char *>(dir) + s.table_off);
    *dir_out = dir;
    *page_at_out = page_at;
    *page_out = *reinterpret_cast<void *volatile *>(page_at);
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}

// The slot table: page = dir[+table_off]. The memory behind the slots must be committed and writable.
bool ResolveSlots(HotSlot &s) {
  void *dir = nullptr, *page = nullptr;
  void **page_at = nullptr;
  if (!ReadDirPage(s, &dir, &page_at, &page)) {
    g_last_fail = "the game's slot directory is not there (null)";
    return false;
  }
  if (!page) {
    g_last_fail = "the slot page is not there (no hot-fix loaded in this session)";
    return false;
  }
  __try {
    char *table = static_cast<char *>(page);
    MEMORY_BASIC_INFORMATION mbi = {};
    if (!VirtualQuery(table + s.fn_off, &mbi, sizeof(mbi)) || mbi.State != MEM_COMMIT || !(mbi.Protect & (PAGE_READWRITE | PAGE_WRITECOPY)) ||
        static_cast<size_t>(static_cast<char *>(mbi.BaseAddress) + mbi.RegionSize - (table + s.fn_off)) < 16) {
      g_last_fail = "the memory behind the slots is not committed/writable";
      return false;
    }
    s.fn_slot = reinterpret_cast<void **>(table + s.fn_off);
    s.ctx_slot = reinterpret_cast<void **>(table + s.ctx_off);
    s.dir = dir;
    s.page = page;
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    g_last_fail = "the slot page could not be read";
    return false;
  }
}

bool g_parsed = false;
void *g_device_info = nullptr, *g_device_info_manager = nullptr, *g_ui_const = nullptr, *g_notch_adapter = nullptr;

bool Prepare() {
  if (g_parsed)
    return g_user.found && g_safe.found;
  FindModules();
  if (!ResolveUi() || !ResolveRefl()) {
    g_last_fail = "the game's UI classes are not resolvable yet";
    return false;
  }
  g_device_info = FindClassAnywhere("Beyond", "DeviceInfo");
  if (!g_device_info) {
    g_last_fail = "Beyond.DeviceInfo is not loaded yet";
    return false;
  }
  g_parsed = true;
  g_device_info_manager = FindClassAnywhere("Beyond", "DeviceInfoManager");
  g_ui_const = FindClassAnywhere("Beyond.UI", "UIConst");
  g_notch_adapter = FindClassAnywhere("Beyond.UI", "NotchAdapter");
  g_user.what = "DeviceInfo.get_userPlatform";
  g_safe.what = "DeviceInfo.get_safeArea";
  const void *m_user = StaticMethod(g_device_info, "get_userPlatform", 0);
  const void *m_safe = StaticMethod(g_device_info, "get_safeArea", 0);
  const bool a = ParseHotSlot(m_user, &g_user), b = ParseHotSlot(m_safe, &g_safe);
  Log("cloud: hot-fix slots read from the machine code: %s %s (global %llx, table offset +0x%x, function slot +0x%x, context slot +0x%x); %s %s (global %llx, table offset +0x%x, function slot +0x%x, context slot +0x%x)",
      g_user.what, a ? "found" : "NOT found", static_cast<unsigned long long>(g_user.global), g_user.table_off, g_user.fn_off, g_user.ctx_off, g_safe.what, b ? "found" : "NOT found",
      static_cast<unsigned long long>(g_safe.global), g_safe.table_off, g_safe.fn_off, g_safe.ctx_off);
  if (!(a && b))
    g_last_fail = "the hot-fix check was not found in the machine code of the two methods";
  __try {
    void *obj = *reinterpret_cast<void **>(g_user.global);
    void *table = obj ? *reinterpret_cast<void **>(static_cast<char *>(obj) + g_user.table_off) : nullptr;
    Log("cloud: the slot directory is %p, its page entry (+0x%x) is %p; slot values now: userPlatform %p, safeArea %p", obj, g_user.table_off, table,
        table ? *reinterpret_cast<void **>(static_cast<char *>(table) + g_user.fn_off) : nullptr, table ? *reinterpret_cast<void **>(static_cast<char *>(table) + g_safe.fn_off) : nullptr);
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    Log("cloud: the slot directory could not be read");
  }
  double pc = 0;
  if (g_ui_const && StaticNumber(g_ui_const, "PC_REFERENCE_RESOLUTION_SCALE", &pc, nullptr) && pc > 1.0 && pc < 3.0)
    g_cloud_pc_scale = static_cast<float>(pc);
  return a && b;
}

// a method result / field value as text: numbers, bool, Vector2/3/4, Rect; anything else as "?"
std::string ValueText(const void *type, const void *raw) {
  char buf[96] = "?";
  if (!type || !raw)
    return buf;
  const int k = TypeKind(type);
  const char *tn = g_refl.type_name ? g_refl.type_name(type) : nullptr;
  const float *f = static_cast<const float *>(raw);
  if (k == 12)
    snprintf(buf, sizeof(buf), "%.4f", f[0]);
  else if (k == 13)
    snprintf(buf, sizeof(buf), "%.4f", *static_cast<const double *>(raw));
  else if (k == 8)
    snprintf(buf, sizeof(buf), "%d", *static_cast<const int *>(raw));
  else if (k == 2)
    snprintf(buf, sizeof(buf), "%s", *static_cast<const unsigned char *>(raw) ? "true" : "false");
  else if (k == 17 && tn && (strstr(tn, "Vector2")))
    snprintf(buf, sizeof(buf), "(%.4f, %.4f)", f[0], f[1]);
  else if (k == 17 && tn && strstr(tn, "Vector3"))
    snprintf(buf, sizeof(buf), "(%.4f, %.4f, %.4f)", f[0], f[1], f[2]);
  else if (k == 17 && tn && (strstr(tn, "Vector4") || strstr(tn, "Rect")))
    snprintf(buf, sizeof(buf), "(%.4f, %.4f, %.4f, %.4f)", f[0], f[1], f[2], f[3]);
  else
    snprintf(buf, sizeof(buf), "? (%s)", tn ? tn : "type");
  return buf;
}
std::string CallText(const void *m, void *obj) {
  if (!m || !g_refl.ok)
    return "-";
  void *boxed = nullptr;
  if (!Invoke0(m, obj, &boxed))
    return "(raised)";
  return ValueText(g_ui.ret_type(m), Unboxed(boxed));
}
bool SmallValueKind(const void *type) {
  const int k = TypeKind(type);
  if (k == 2 || (k >= 4 && k <= 13))
    return true;
  const char *tn = (k == 17 && g_refl.type_name) ? g_refl.type_name(type) : nullptr;
  return tn && (strstr(tn, "Vector2") || strstr(tn, "Vector3") || strstr(tn, "Vector4") || strstr(tn, "Rect"));
}

// root canvas scale of UIRoot, CanvasRoot and one open menu panel, and the notch padding the game applies (DeviceInfoManager + NotchAdapter components)
LONG g_root_logs = 0;
void LogRootScales(const char *when) {
  if (!g_ui.resolved || !g_ui.canvas_class || !g_ui.m_c_get_sf || InterlockedIncrement(&g_root_logs) > 12)
    return;
  static const void *m_active = nullptr;
  static bool active_tried = false;
  if (!active_tried && g_managed.game_assembly) {
    active_tried = true;
    auto mfn = reinterpret_cast<MethodFromName>(GetProcAddress(g_managed.game_assembly, "il2cpp_class_get_method_from_name"));
    m_active = mfn ? mfn(g_ui.canvas_class, "get_isActiveAndEnabled", 0) : nullptr;
  }
  void **canvases = nullptr;
  const size_t n = FindObjects(g_ui.canvas_class, &canvases);
  std::string out;
  bool have_root = false, have_canvas_root = false, have_panel = false;
  for (size_t i = 0; i < n && i < 4000; ++i) {
    if (!canvases[i])
      continue;
    char name[96];
    NameOf(canvases[i], name, sizeof(name));
    const bool is_root = strcmp(name, "UIRoot") == 0, is_croot = strcmp(name, "CanvasRoot") == 0;
    bool is_panel = false;
    if (!is_root && !is_croot && !have_panel && strstr(name, "Panel") && strcmp(name, "TouchPanel") != 0 && strcmp(name, "MouseIconHintPanel") != 0 && strcmp(name, "CommonTipsPanel") != 0 &&
        strcmp(name, "CommonDragPanel") != 0) {
      int active = 1;
      if (m_active)
        ReadBool(m_active, canvases[i], &active);
      is_panel = active == 1;
    }
    if ((is_root && have_root) || (is_croot && have_canvas_root) || !(is_root || is_croot || is_panel))
      continue;
    float sf = 0;
    ReadFloat(g_ui.m_c_get_sf, canvases[i], &sf);
    int root = -1;
    if (g_ui.m_c_root)
      ReadBool(g_ui.m_c_root, canvases[i], &root);
    char part[160];
    snprintf(part, sizeof(part), "%s'%s' scaleFactor %.3f%s", out.empty() ? "" : "; ", name, sf, root == 1 ? " (root)" : (root == 0 ? " (nested)" : ""));
    out += part;
    have_root |= is_root;
    have_canvas_root |= is_croot;
    have_panel |= is_panel;
  }
  if (!have_panel)
    out += out.empty() ? "no open menu panel yet" : "; no open menu panel yet";
  Log("UI scale (%s): %zu canvas(es): %s", when, n, out.c_str());
  // the notch padding as the game applies it
  std::string notch = "DeviceInfoManager.NotchPadding=" + CallText(g_device_info_manager ? StaticMethod(g_device_info_manager, "NotchPadding", 0) : nullptr, nullptr) +
                      " StandardNotchPadding=" + CallText(g_device_info_manager ? StaticMethod(g_device_info_manager, "StandardNotchPadding", 0) : nullptr, nullptr) +
                      " NotchPaddingInCanvas=" + CallText(g_device_info_manager ? StaticMethod(g_device_info_manager, "NotchPaddingInCanvas", 0) : nullptr, nullptr);
  if (g_notch_adapter && g_refl.ok && g_ui.field_get) {
    void *f_margin = g_refl.get_field(g_notch_adapter, "selfMarginSize");
    const void *ft = f_margin ? g_refl.field_type(f_margin) : nullptr;
    void **adapters = nullptr;
    const size_t an = FindObjects(g_notch_adapter, &adapters);
    char head[64];
    snprintf(head, sizeof(head), " | %zu NotchAdapter(s)", an);
    notch += head;
    for (size_t i = 0, shown = 0; i < an && shown < 4; ++i) {
      if (!adapters[i])
        continue;
      char name[96];
      NameOf(adapters[i], name, sizeof(name));
      std::string v = "?";
      if (f_margin && SmallValueKind(ft)) {
        alignas(16) unsigned char raw[32] = {};
        g_ui.field_get(adapters[i], f_margin, raw);
        v = ValueText(ft, raw);
      }
      notch += std::string(shown ? ", '" : ": '") + name + "' selfMarginSize " + v;
      ++shown;
    }
  }
  if (g_cloud_fallback) {
    char nb[160];
    snprintf(nb, sizeof(nb), " | notch setting written=%d target %.4f original %.4f", g_notch_written ? 1 : 0, g_notch_target, g_notch_orig);
    notch += nb;
  }
  Log("notch (%s): %s", when, notch.c_str());
}

void Verify(const char *when) {
  if (!g_device_info)
    return;
  auto get_bool = [&](const char *name) -> int {
    const void *m = StaticMethod(g_device_info, name, 0);
    int v = -1;
    return (m && ReadBool(m, nullptr, &v)) ? v : -1;
  };
  int platform = -1;
  if (const void *m = StaticMethod(g_device_info, "get_userPlatform", 0))
    ReadInt(m, nullptr, &platform);
  float rect[4] = {-1, -1, -1, -1};
  bool have_rect = false;
  if (const void *m = StaticMethod(g_device_info, "get_safeArea", 0)) {
    void *boxed = nullptr;
    if (Invoke0(m, nullptr, &boxed) && boxed)
      if (const float *r = static_cast<const float *>(Unboxed(boxed))) {
        memcpy(rect, r, sizeof(rect));
        have_rect = true;
      }
  }
  Vec2 notch{-1, -1}, std_notch{-1, -1};
  if (g_device_info_manager) {
    if (const void *m = StaticMethod(g_device_info_manager, "NotchPadding", 0))
      ReadVec2(m, nullptr, &notch);
    if (const void *m = StaticMethod(g_device_info_manager, "StandardNotchPadding", 0))
      ReadVec2(m, nullptr, &std_notch);
  }
  float scale = -1;
  if (g_ui_const)
    if (const void *m = StaticMethod(g_ui_const, "GetResolutionScale", 0))
      ReadFloat(m, nullptr, &scale);
  Log("cloud state (%s): userPlatform=%d isPCUser=%d isMobileUser=%d isIOSUser=%d | DeviceInfo.safeArea=%s(x %.0f, y %.0f, w %.0f, h %.0f) | DeviceInfoManager.NotchPadding=(%.4f, %.4f) StandardNotchPadding=(%.4f, %.4f) | UIConst.GetResolutionScale=%.3f | hook calls: userPlatform %ld, safeArea %ld",
      when, platform, get_bool("get_isPCUser"), get_bool("get_isMobileUser"), get_bool("get_isIOSUser"), have_rect ? "" : "(unreadable) ", rect[0], rect[1], rect[2], rect[3], notch.x, notch.y, std_notch.x, std_notch.y, scale,
      g_user_calls, g_safe_calls);
}

// ctx first, then the function (the order of the game's own setter); a slot that held a foreign function keeps it as old_fn (put back when cloud_mode goes off)
void FillSlot(HotSlot &s, void *hook) {
  void *cur = *s.fn_slot;
  if (cur != hook) {
    s.old_fn = cur;
    if (cur)
      Log("cloud: note: the %s slot was not empty (%p); the game or a hot-fix uses it; it is put back when cloud_mode goes off", s.what, cur);
  }
  *s.ctx_slot = nullptr;
  MemoryBarrier();
  InterlockedExchangePointer(s.fn_slot, hook);
}

bool FillBoth() {
  HotSlot u = g_user, s = g_safe;
  if (!ResolveSlots(u) || !ResolveSlots(s))
    return false;
  g_user = u;
  g_safe = s;
  __try {
    FillSlot(g_user, reinterpret_cast<void *>(&HookUserPlatform));
    FillSlot(g_safe, reinterpret_cast<void *>(&HookSafeArea));
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    g_last_fail = "writing the slots raised an exception";
    return false;
  }
  return true;
}

void ScheduleStateLogs() {
  g_verify_due = g_pumps + 4;
  g_verify_count = 0;
}

void Install() {
  if (!FillBoth()) {
    static LONG said = 0;
    if (InterlockedIncrement(&said) <= 3)
      Log("cloud: the slots could not be filled yet (%s); trying again", g_last_fail);
    return;
  }
  g_installed = true;
  g_cloud_active = true;
  g_cloud_since = g_pumps;
  g_installed_at = g_pumps;
  ScheduleStateLogs();
  Log("cloud: PATH A active (the game's own mobile UI): DeviceInfo.get_userPlatform -> %ld, DeviceInfo.get_safeArea -> the device insets; slot page %p; asking the UI to lay out again", g_user_platform,
      g_user.page);
  ForceRelayout("cloud_mode went on");
}

// every second while installed: the game may have put in its own page or directory (a hot-fix loaded, metadata reloaded) or emptied the slots (its "clear all"); the slots are filled again
void Watch() {
  void *dir = nullptr, *page = nullptr;
  void **page_at = nullptr;
  const bool readable = ReadDirPage(g_user, &dir, &page_at, &page);
  if (!readable) {  // the directory is gone (the game's shutdown frees it): nothing more is written
    Log("cloud: the game's slot directory is gone (shutdown?); the Runtime stops touching it");
    g_installed = false;
    g_cloud_active = false;
    g_user.fn_slot = g_user.ctx_slot = g_safe.fn_slot = g_safe.ctx_slot = nullptr;
    return;
  }
  const char *why = nullptr;
  if (dir != g_user.dir)
    why = "the game replaced its slot directory";
  else if (page != g_user.page)
    why = page ? "the game put in its own slot page" : "the slot page entry was emptied";
  else {
    __try {
      if (*g_user.fn_slot != reinterpret_cast<void *>(&HookUserPlatform) || *g_safe.fn_slot != reinterpret_cast<void *>(&HookSafeArea))
        why = "a slot was emptied or overwritten";
    } __except (EXCEPTION_EXECUTE_HANDLER) {
      why = "the slot page is not readable";
    }
  }
  if (!why)
    return;
  void *old_dir = g_user.dir, *old_page = g_user.page;
  const bool ok = FillBoth();
  const LONG n = InterlockedIncrement(&g_reinstalls);
  if (n <= 8)
    Log("cloud: %s (directory %p -> %p, page %p -> %p): slots %s", why, old_dir, g_user.dir, old_page, g_user.page, ok ? "filled again" : "NOT filled again");
  if (!ok) {
    g_installed = false;
    g_cloud_active = false;
  }
}

void Uninstall() {
  if (!g_installed)
    return;
  void *dir = nullptr, *page = nullptr;
  void **page_at = nullptr;
  if (ReadDirPage(g_user, &dir, &page_at, &page) && dir == g_user.dir && page == g_user.page) {
    __try {
      if (g_user.fn_slot && *g_user.fn_slot == reinterpret_cast<void *>(&HookUserPlatform))
        InterlockedExchangePointer(g_user.fn_slot, g_user.old_fn);
      if (g_safe.fn_slot && *g_safe.fn_slot == reinterpret_cast<void *>(&HookSafeArea))
        InterlockedExchangePointer(g_safe.fn_slot, g_safe.old_fn);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
  }
  g_installed = false;
  g_cloud_active = false;
  Log("cloud: cloud_mode is off: both slots are empty again (the page stays); asking the UI to lay out again");
  ForceRelayout("cloud_mode went off");
}

// the slots could not be filled in time: the PC path (UI size knob and notch setting, as with cloud_mode=off) for the rest of the session
void Fallback() {
  g_cloud_fallback = true;
  g_ui_knob_mode = 0;
  g_flag_notch_write = g_ini_notch_write;
  g_notch_reeval = true;
  ScheduleStateLogs();
  Log("cloud: PATH B active (fallback, the PC UI with the size knob and the notch setting): the slots could not be filled within %ld s of the first attempt (%s); notch_write=%d; stays for this session",
      kFallbackPumps / 4, g_last_fail, g_flag_notch_write ? 1 : 0);
}

void StateLogs() {
  if (!g_verify_due || g_pumps < g_verify_due)
    return;
  ++g_verify_count;
  const char *when = g_verify_count == 1 ? "1 s after the path went active" : (g_verify_count == 2 ? "10 s later" : "30 s later");
  if (g_installed)
    Verify(when);
  LogUiState(g_verify_count == 1 ? "cloud: 1 s after" : (g_verify_count == 2 ? "cloud: 10 s later" : "cloud: 30 s later"));
  LogRootScales(when);
  g_verify_due = g_verify_count >= 3 ? 0 : g_pumps + (g_verify_count == 1 ? 36 : 80);
}

void Tick() {
  if (!g_shared)
    return;
  // the platform of the client device: the stream's device profile (iPhone / iPad rows -> iOS, Android rows -> Android)
  if (g_have_profile)
    g_user_platform = (g_profile.name.rfind("android", 0) == 0) ? 11 : 8;
  if (g_cloud_mode && !g_installed && !g_cloud_fallback) {
    if (g_pumps % 4 == 3 && g_have_profile) {
      if (g_try_since < 0)
        g_try_since = g_pumps;
      if (Prepare())
        Install();
      if (!g_installed && g_pumps - g_try_since >= kFallbackPumps)
        Fallback();
    }
  } else if (!g_cloud_mode && g_installed) {
    Uninstall();
  }
  if (!g_cloud_mode)
    g_try_since = -1;
  if (g_installed && g_pumps % 4 == 1)
    Watch();
  if (g_installed) {  // the stream's safe area changed (safe_area in the ini, another device): the UI is asked to read it again
    static RectF applied = {-1, -1, -1, -1};
    const RectF now = CurrentRect();
    if (applied.w >= 0 && (now.x != applied.x || now.w != applied.w || now.y != applied.y || now.h != applied.h)) {
      Log("cloud: the device safe area changed to Rect(x %.0f, y %.0f, w %.0f, h %.0f); asking the UI to lay out again", now.x, now.y, now.w, now.h);
      ForceRelayout("the device safe area changed");
      ScheduleStateLogs();
    }
    applied = now;
  }
  if (g_installed || g_cloud_fallback)
    StateLogs();
}

}  // namespace cl
