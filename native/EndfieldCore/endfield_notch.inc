// Notch padding through the game's own setting (included into endfield_runtime.cpp, inside its anonymous namespace, after endfield_ui_size.inc). The PC path only: with cloud_mode the
// game computes the padding itself from the device safe area (endfield_cloud.inc).
//
// Beyond.GameSetting has a persisted "video notch padding" setting (ID_VIDEO_NOTCH_PADDING). Beyond.UI.NotchAdapter lays out the side UI from it (via DeviceInfoManager.NotchPadding*), and
// ON_NOTCH_PADDING_CHANGED tells the UI. The Runtime reads it, logs units, writes it ONCE from the device profile through the game's own setter and restores the original at session end.
enum NotchPhase { kNotchWait, kNotchDone, kNotchSkipped };
NotchPhase g_notch = kNotchWait;
bool g_notch_written = false, g_notch_restored = false;
double g_notch_orig = 0, g_notch_target = 0;
std::string g_notch_id_text;
const void *g_notch_setter = nullptr;
bool g_finalizing = false;
LONG g_notch_attempt = 0;
std::wstring g_notch_file;

// ---- Beyond.GameSetting is a static class keyed by string setting ids: SetSettingValueFloat(string settingId, float value). The id for the current screen shape comes from GetCurVideoNotchPaddingId()
// (a string such as "video_notch_padding218": the aspect bucket is part of it).
using StringNewFn = void *(__cdecl *)(const char *);
void *NewManagedString(const std::string &text) {
  static StringNewFn fn = nullptr;
  if (!fn && g_managed.game_assembly)
    fn = reinterpret_cast<StringNewFn>(GetProcAddress(g_managed.game_assembly, "il2cpp_string_new"));
  return fn ? fn(text.c_str()) : nullptr;
}
// the id of the notch setting for the current screen shape; the constant ID_VIDEO_NOTCH_PADDING (a string) when the method is not callable
std::string FetchNotchId(void *setting) {
  if (const void *m = StaticMethod(setting, "GetCurVideoNotchPaddingId", 0)) {
    void *exception = nullptr;
    void *result = g_managed.runtime_invoke(m, nullptr, nullptr, &exception);
    const std::string id = exception ? "" : Utf8Of(result);
    if (!id.empty())
      return id;
  }
  if (void *field = g_refl.get_field(setting, "ID_VIDEO_NOTCH_PADDING")) {
    void *value = nullptr;
    g_refl.static_get(field, &value);
    return Utf8Of(value);
  }
  return "";
}
bool CallSetFloat(const void *setter, const std::string &id, float value, bool *accepted) {
  void *id_object = NewManagedString(id);
  if (!id_object || !setter)
    return false;
  float v = value;
  void *args[2] = {id_object, &v};
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(setter, nullptr, args, &exception);
  if (exception)
    return false;
  if (accepted) {
    const unsigned char *raw = static_cast<const unsigned char *>(Unboxed(boxed));
    *accepted = raw ? *raw != 0 : true;
  }
  return true;
}

// the scale factor of the menu root canvas ('UIRoot'): canvas units are screen pixels / this
float UiRootScale() {
  if (!g_ui.scaler_class || !g_ui.f_scaler_canvas || !g_ui.m_c_get_sf || !g_ui.m_name)
    return 0.0f;
  void **scalers = nullptr;
  const size_t n = FindObjects(g_ui.scaler_class, &scalers);
  for (size_t i = 0; i < n; ++i) {
    char name[96];
    if (!scalers[i] || strcmp(NameOf(scalers[i], name, sizeof(name)), "UIRoot") != 0)
      continue;
    void *canvas = nullptr;
    g_ui.field_get(scalers[i], g_ui.f_scaler_canvas, &canvas);
    float sf = 0;
    if (canvas && ReadFloat(g_ui.m_c_get_sf, canvas, &sf) && sf > 0.1f)
      return sf;
  }
  return 0.0f;
}

void NotchFileSave(double original, const std::string &id) {
  if (g_notch_file.empty())
    return;
  char text[320];
  const int n = snprintf(text, sizeof(text), "original=%.6f\nid=%s\n", original, id.c_str());
  HANDLE f = CreateFileW(g_notch_file.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (f != INVALID_HANDLE_VALUE) {
    DWORD w = 0;
    WriteFile(f, text, n, &w, nullptr);
    CloseHandle(f);
  }
}

void RestoreNotch() {
  if (!g_notch_written || g_notch_restored || !g_notch_setter)
    return;
  g_notch_restored = true;
  bool accepted = false;
  const bool ok = CallSetFloat(g_notch_setter, g_notch_id_text, static_cast<float>(g_notch_orig), &accepted);
  Log("notch: session end: restoring the original notch padding %.4f for '%s' through the game's setter: %s", g_notch_orig, g_notch_id_text.c_str(), ok ? "called" : "FAILED");
  if (ok && !g_notch_file.empty())
    DeleteFileW(g_notch_file.c_str());
}

// safe_area changed to off (or to no insets) while a value is written: the original goes back through the game's setter, once
void RestoreNotchLive(const char *why) {
  if (!g_notch_written || !g_notch_setter)
    return;
  bool accepted = false;
  const bool ok = CallSetFloat(g_notch_setter, g_notch_id_text, static_cast<float>(g_notch_orig), &accepted);
  Log("notch: %s: the original notch padding %.4f was put back through the game's setter: %s", why, g_notch_orig, ok ? "called" : "FAILED");
  if (ok) {
    g_notch_written = false;
    if (!g_notch_file.empty())
      DeleteFileW(g_notch_file.c_str());
    ForceRelayout("the notch padding was restored");
  }
}

void EnsureNotch() {
  if (g_notch_reeval) {
    g_notch_reeval = false;
    if (g_notch == kNotchDone || g_notch == kNotchSkipped)
      g_notch = kNotchWait;
    g_notch_attempt = 0;
  }
  if (!g_flag_notch_write) {
    if (g_notch_written && g_pumps % 4 == 2)
      RestoreNotchLive("safe_area=off");
    if (g_notch_written == false && g_notch == kNotchDone)
      g_notch = kNotchWait;
    return;
  }
  if (g_notch != kNotchWait || !g_have_profile)
    return;
  if (g_pumps % 4 != 2)
    return;
  if (!ProfileHasInsets(g_profile)) {
    RestoreNotchLive("the profile has no side inset");
    g_notch = kNotchSkipped;
    Log("notch: the device profile '%s' has no side inset; the game's notch padding is left alone", g_profile.name.c_str());
    return;
  }
  FindModules();
  if (!ResolveUi() || !ResolveRefl())
    return;
  if (++g_notch_attempt > 600) {
    g_notch = kNotchSkipped;
    Log("notch: gave up waiting for Beyond.GameSetting after 150 s");
    return;
  }
  void *setting = FindClassAnywhere("Beyond", "GameSetting");
  void *helper_cls = FindClassAnywhere("Beyond.Gameplay", "GameSettingHelper");
  if (!setting || !helper_cls)
    return;
  // wait for the settings table
  if (const void *is_init = StaticMethod(setting, "get_tableIsInit", 0)) {
    double v = 0;
    bool has = false;
    if (!CallNumeric(is_init, nullptr, {}, &v, &has) || v == 0)
      return;
  }
  static bool logged_methods = false;
  if (!logged_methods) {
    logged_methods = true;
    for (const char *n : {"get_videoNotchPadding", "get_videoCachedNotchPadding", "GetCurVideoNotchPaddingId", "get_videoNotchPaddingSetting", "SetSettingValueFloat"})
      LogMethods(setting, "GameSetting", n);
    for (const char *n : {"GetGameSettingDefaultNotchPadding", "GetGameSettingNotchPaddingFromCanvasPadding", "GetGameSettingCanvasPaddingFromNotchPadding"})
      LogMethods(helper_cls, "GameSettingHelper", n);
  }
  const std::string id = FetchNotchId(setting);
  if (id.empty())
    return;  // the setting id is not available yet: asked again

  double max_value = 0;
  int max_kind = 0;
  const bool have_max = StaticNumber(setting, "DEFAULT_MAX_VALUE_VIDEO_NOTCH_PADDING", &max_value, &max_kind);
  double cur = 0, cached = 0, def = 0;
  bool has = false;
  const void *get_pad = StaticMethod(setting, "get_videoNotchPadding", 0);
  const void *get_cached = StaticMethod(setting, "get_videoCachedNotchPadding", 0);
  const bool have_cur = get_pad && CallNumeric(get_pad, nullptr, {}, &cur, &has) && has;
  const bool have_cached = get_cached && CallNumeric(get_cached, nullptr, {}, &cached, &has) && has;
  const void *get_def = StaticMethod(helper_cls, "GetGameSettingDefaultNotchPadding", 0);
  const bool have_def = get_def && CallNumeric(get_def, nullptr, {}, &def, &has) && has;
  Log("notch: setting id '%s' (maximum %s%.1f); current videoNotchPadding=%s%.4f cached=%s%.4f default=%s%.4f", id.c_str(), have_max ? "" : "(unreadable) ", max_value, have_cur ? "" : "(unreadable) ", cur,
      have_cached ? "" : "(unreadable) ", cached, have_def ? "" : "(unreadable) ", def);

  // the geometry of the menu canvas: canvas units = screen pixels / its scale factor
  float il = 0, ir = 0, it = 0, ib = 0;
  const int W = g_last_w, H = g_last_h;
  ComputeInsets(g_profile, W, H, &il, &ir, &it, &ib);
  const double side_px = (il + ir) / 2.0;
  const int long_side = W > H ? W : H, short_side = W > H ? H : W;
  float sf = UiRootScale();
  const char *sf_from = "the UIRoot canvas";
  if (sf <= 0.0f) {
    void **helpers = nullptr;
    Vec2 sv{0, 0};
    float sc = 0;
    if (g_ui.helper_class && FindObjects(g_ui.helper_class, &helpers) > 0 && helpers[0] && HelperStd(helpers[0], &sv, &sc) && sv.y > 0 && sc > 0) {
      sf = static_cast<float>(short_side) / (sv.y * sc);
      sf_from = "the helper's standard resolution (UIRoot not found)";
    } else {
      sf = static_cast<float>(short_side) / 1080.0f;
      sf_from = "1080 (nothing else readable)";
    }
  }
  const double canvas_w = long_side / static_cast<double>(sf), canvas_pad = side_px / sf;
  const void *from_canvas = StaticMethod(helper_cls, "GetGameSettingNotchPaddingFromCanvasPadding", 2);
  const void *to_canvas = StaticMethod(helper_cls, "GetGameSettingCanvasPaddingFromNotchPadding", 2);
  double target = canvas_pad;
  bool converted = false;
  if (from_canvas) {
    bool h = false;
    double conv = 0;
    if (CallNumeric(from_canvas, nullptr, {canvas_pad, canvas_w}, &conv, &h) && h) {
      target = conv;
      converted = true;
    }
  }
  double back_canvas = 0;
  bool have_back_canvas = false;
  if (to_canvas) {
    bool h = false;
    have_back_canvas = CallNumeric(to_canvas, nullptr, {target, canvas_w}, &back_canvas, &h) && h;
  }
  if (have_max && max_value > 0 && target > max_value) {
    Log("notch: target %.4f is above the setting's maximum %.4f; clamped", target, max_value);
    target = max_value;
  }
  Log("notch: profile %s: side inset %.1f px on a %d x %d screen; canvas scale %.3f from %s -> canvas width %.1f, padding %.2f canvas units -> setting value %.4f (%s; the game's own inverse gives %s%.2f canvas units back)",
      g_profile.name.c_str(), side_px, W, H, sf, sf_from, canvas_w, canvas_pad, target, converted ? "converted by the game" : "NOT converted", have_back_canvas ? "" : "(n/a) ", back_canvas);

  const void *setter = nullptr;
  for (const void *m : MethodsNamed(setting, "SetSettingValueFloat"))
    if (!g_refl.is_instance(m) && g_refl.param_count(m) == 2) {
      setter = m;
      break;
    }
  if (!setter || !have_cur) {
    g_notch = kNotchSkipped;
    Log("notch: cannot write: setter %s, current value %s (see the signature lines above); nothing was changed", setter ? "found" : "NOT found", have_cur ? "readable" : "NOT readable");
    return;
  }
  // a previous session that did not restore: its file holds the real original
  wchar_t path[MAX_PATH] = {};
  GetModuleFileNameW(g_module, path, MAX_PATH);
  std::wstring dir = path;
  g_notch_file = dir.substr(0, dir.find_last_of(L"\\/") + 1) + L"endfield-notch-original.txt";
  double original = g_notch_written ? g_notch_orig : cur;
  if (!g_notch_written) {
    std::string old = ReadWholeFile(g_notch_file);
    const size_t at = old.find("original=");
    if (at != std::string::npos) {
      original = atof(old.c_str() + at + 9);
      Log("notch: a previous session did not restore the notch padding; its original value %.4f is used as the original (current value %.4f)", original, cur);
    }
  }
  if (std::fabs(cur - target) < 0.0005) {
    g_notch = kNotchDone;
    Log("notch: the setting already holds %.4f; nothing to write", cur);
    return;
  }
  NotchFileSave(original, id);
  g_notch_orig = original;
  g_notch_id_text = id;
  g_notch_target = target;
  g_notch_setter = setter;
  bool accepted = false;
  const bool ok = CallSetFloat(setter, id, static_cast<float>(target), &accepted);
  double back = 0, back_cached = 0;
  const bool have_back = get_pad && CallNumeric(get_pad, nullptr, {}, &back, &has) && has;
  const bool have_back_cached = get_cached && CallNumeric(get_cached, nullptr, {}, &back_cached, &has) && has;
  g_notch_written = ok;
  g_notch = kNotchDone;
  Log("notch: WROTE %.4f for '%s' through %s: call %s, the game says %s; read back videoNotchPadding=%s%.4f cached=%s%.4f -> %s", target, id.c_str(), Sig(setter).c_str(), ok ? "returned" : "RAISED an exception",
      accepted ? "accepted" : "refused/false", have_back ? "" : "(unreadable) ", back, have_back_cached ? "" : "(unreadable) ", back_cached,
      (have_back && std::fabs(back - target) < 0.01) ? "in effect" : "NOT in effect (report this)");
  if (!(have_back && std::fabs(back - target) < 0.01)) {
    // the Setting<float> object behind the property: its class and methods, for the next round
    if (const void *get_obj = StaticMethod(setting, "get_videoNotchPaddingSetting", 0)) {
      void *exception = nullptr;
      void *obj = g_managed.runtime_invoke(get_obj, nullptr, nullptr, &exception);
      auto obj_class = reinterpret_cast<void *(__cdecl *)(void *)>(GetProcAddress(g_managed.game_assembly, "il2cpp_object_get_class"));
      auto class_name = reinterpret_cast<const char *(__cdecl *)(void *)>(GetProcAddress(g_managed.game_assembly, "il2cpp_class_get_name"));
      if (obj && !exception && obj_class && class_name) {
        void *k = obj_class(obj);
        Log("notch: videoNotchPaddingSetting is an object of class %s", class_name(k));
        void *iter = nullptr;
        int shown = 0;
        while (const void *m = g_refl.get_methods(k, &iter))
          if (shown++ < 30)
            Log("notch: Setting method: %s", Sig(m).c_str());
      }
    }
  }
  ForceRelayout("the notch padding setting was written");
}

