// UI keys of endfield-ui.ini and the client device profile (included into endfield_runtime.cpp, inside its anonymous namespace, after endfield_il2cpp.inc).
//
// The game runs on a PC but is played on the stream's client device. What the game's own mobile logic needs to know about that device (phone or tablet, its notch, its size class) comes
// from a table keyed by the stream resolution: a client streams at its own native resolution, so the size names the device. endfield-ui.ini can override the safe area.
//
// endfield-ui.ini (written by Sparxie before the launch and on every save while the game runs; see UI_INI_CONTRACT.md):
//   cloud_mode=on (default) | off       the game's own cloud-gaming inputs: user platform and device safe area of the client (endfield_cloud.inc)
//   ui_scale=auto (default) | off | <1.00-1.80>   UI size relative to the PC build's default
//   safe_area=auto (default) | off | <left>,<right>   side insets in stream pixels
//   observe=1 (default) | 0              read-only log lines about new canvases

// ---- shared state of the UI parts (UI size, notch, cloud mode)
bool g_flag_observe = true;       // observe=0: no read-only logging of new canvases / scalers
bool g_flag_notch_write = false;  // the notch padding goes through the game's own setting (the PC path only; in cloud mode the game computes it from the device safe area)
bool g_ini_notch_write = true;    // what the PC path does with the notch: false when safe_area=off
int g_ui_knob_mode = 0;           // 0: the PC reference scale first, then the standard resolution; 2: the standard resolution only (cloud mode: the base is already the phone size)
float g_ui_rel = 0.0f;            // ui_scale=<f>: UI size relative to the PC default; 0 = auto (the device profile, else the phone build's size)
bool g_ui_off = false;            // ui_scale=off: the game's own value (applied live: the original is put back)
volatile LONG g_cloud_mode = 0;   // cloud_mode=on: the user-platform input of the game's cloud design (endfield_cloud.inc)
bool g_cloud_seen = false;        // the first config was read (the cloud_mode line is logged on every change, the first one included)
bool g_cloud_active = false;      // the two hot-fix slots are filled
LONG g_cloud_since = 0;
bool g_cloud_fallback = false;    // cloud_mode=on but the slots could not be filled in time: the PC path (size knob, notch setting) for the session
float g_cloud_pc_scale = 1.25f;   // UIConst.PC_REFERENCE_RESOLUTION_SCALE
bool g_force_profile = false;     // the ini changed: the device profile is rebuilt on the next pump
bool g_notch_reeval = false;      // safe_area or the UI size changed: the notch value is worked out again
std::string g_safe_cfg_key;
float g_ui_logged = -1.0f;
int g_last_w = 0, g_last_h = 0;

// ---- the device table. Landscape pixels; width_pt = landscape width in points; inset_pt = the landscape side inset (left = right) of the safe area in points. The game insets sides only.
struct Dev { const char *key, *name, *cls; int w, h; float width_pt, inset_pt; float ui_scale = 0; };  // ui_scale: the UI size for this device with ui_scale=auto (0 = the phone build's size)
const Dev kDevices[] = {
    {"iphone-se", "iPhone SE (2/3), 8", "phone", 1334, 750, 667, 0},
    {"iphone-x", "iPhone X / XS / 11 Pro", "phone", 2436, 1125, 812, 44},
    {"iphone-xr", "iPhone XR / 11", "phone", 1792, 828, 896, 44},
    {"iphone-xs-max", "iPhone XS Max / 11 Pro Max", "phone", 2688, 1242, 896, 44},
    {"iphone-12-mini", "iPhone 12 mini / 13 mini", "phone", 2340, 1080, 780, 50},
    {"iphone-12", "iPhone 12 / 12 Pro / 13 / 13 Pro / 14 / 16e", "phone", 2532, 1170, 844, 47},
    {"iphone-12-pro-max", "iPhone 12 Pro Max / 13 Pro Max / 14 Plus", "phone", 2778, 1284, 926, 47},
    {"iphone-14-pro", "iPhone 14 Pro / 15 / 15 Pro / 16", "phone", 2556, 1179, 852, 59},
    {"iphone-14-pro-max", "iPhone 14 Pro Max / 15 Plus / 15 Pro Max / 16 Plus", "phone", 2796, 1290, 932, 59},
    {"iphone-16-pro", "iPhone 16 Pro / 17 / 17 Pro", "phone", 2622, 1206, 874, 62},
    {"iphone-air", "iPhone Air", "phone", 2736, 1260, 912, 62},
    {"iphone-17-pro-max", "iPhone 17 Pro Max / 16 Pro Max", "phone", 2868, 1320, 956, 62, 1.35f},
    {"ipad-9", "iPad 9th gen", "pad", 2160, 1620, 1080, 0},
    {"ipad-mini", "iPad mini 6 / 7", "pad", 2266, 1488, 1133, 0},
    {"ipad-air-11", "iPad Air 11 / iPad 10", "pad", 2360, 1640, 1180, 0},
    {"ipad-pro-11", "iPad Pro 11 (M1/M2)", "pad", 2388, 1668, 1194, 0},
    {"ipad-pro-11-m4", "iPad Pro 11 (M4/M5)", "pad", 2420, 1668, 1210, 0},
    {"ipad-air-13", "iPad Air 13 / iPad Pro 12.9", "pad", 2732, 2048, 1366, 0},
    {"ipad-pro-13", "iPad Pro 13 (M4/M5)", "pad", 2752, 2064, 1376, 0},
    {"android-phone-1080p", "Android phone 2400x1080 (e.g. Pixel 8a)", "phone", 2400, 1080, 0, 0},
    {"android-phone-2412", "Android phone 2412x1080", "phone", 2412, 1080, 0, 0},
    {"android-phone-3120", "Android phone 3120x1440 (Galaxy S Ultra)", "phone", 3120, 1440, 0, 0},
    {"android-phone-3200", "Android phone 3200x1440", "phone", 3200, 1440, 0, 0},
    {"android-phone-2992", "Pixel 9 Pro XL class 2992x1344", "phone", 2992, 1344, 0, 0},
    {"android-tablet-2560", "Android tablet 2560x1600 (Galaxy Tab S9 / Pixel Tablet)", "pad", 2560, 1600, 0, 0},
    {"android-tablet-2960", "Galaxy Tab S9 Ultra 2960x1848", "pad", 2960, 1848, 0, 0},
    {"android-tablet-2000", "Android tablet 2000x1200", "pad", 2000, 1200, 0, 0},
};

// safe_area=<left>,<right> from the ini: the side insets in stream pixels
struct ProfileSpec {
  bool given = false;
  float l_px = 0, r_px = 0;
};
struct Profile {
  std::string source = "none", name = "none", cls = "";
  float width_pt = 0, l_pt = 0, r_pt = 0, l_px = 0, r_px = 0, ui_scale = 0;
  bool use_px = false;
  bool Same(const Profile &o) const {
    return source == o.source && ui_scale == o.ui_scale && name == o.name && cls == o.cls && width_pt == o.width_pt && l_pt == o.l_pt && r_pt == o.r_pt && l_px == o.l_px && r_px == o.r_px && use_px == o.use_px;
  }
};
ProfileSpec g_spec_ini;
Profile g_profile;
bool g_have_profile = false;

// the UI keys of one reading of the ini; the defaults are what a missing key (or a missing file) means
struct UiConfig {
  bool cloud_on = true, observe = true, ui_off = false;
  float ui_rel = 0.0f;
  int safe_mode = 0;  // safe_area: 0 auto, 1 off, 2 explicit pixels
  float safe_l = 0, safe_r = 0;
};

// one line of the ini; false when it is not a UI key
bool ParseUiLine(const char *line, UiConfig &c) {
  if (_strnicmp(line, "observe=", 8) == 0) {
    c.observe = atoi(line + 8) != 0;
  } else if (_strnicmp(line, "cloud_mode=", 11) == 0) {
    c.cloud_on = _strnicmp(line + 11, "off", 3) != 0;
  } else if (_strnicmp(line, "ui_scale=", 9) == 0) {
    const char *v = line + 9;
    c.ui_off = false;
    if (_strnicmp(v, "auto", 4) == 0) {
      c.ui_rel = 0.0f;
    } else if (_strnicmp(v, "off", 3) == 0) {
      c.ui_off = true;
    } else {
      const float f = static_cast<float>(atof(v));
      c.ui_rel = !(f > 0.0f) ? 0.0f : (f < 1.0f ? 1.0f : (f > 1.8f ? 1.8f : f));  // the contract range 1.00 - 1.80 is clamped; text that is not a number means auto
    }
  } else if (_strnicmp(line, "safe_area=", 10) == 0) {
    const char *v = line + 10;
    if (_strnicmp(v, "auto", 4) == 0) {
      c.safe_mode = 0;
    } else if (_strnicmp(v, "off", 3) == 0) {
      c.safe_mode = 1;
    } else {
      c.safe_mode = 2;
      c.safe_l = static_cast<float>(atof(v));
      const char *comma = strchr(v, ',');
      c.safe_r = comma ? static_cast<float>(atof(comma + 1)) : c.safe_l;
    }
  } else {
    return false;
  }
  return true;
}

// the UI keys take effect: changed values are applied once by the parts that own them (UI size, notch, cloud mode)
void ApplyUiConfig(const UiConfig &c) {
  auto clamp_inset = [](float v) { return (v >= 0.0f && v <= 2000.0f) ? v : 0.0f; };
  ProfileSpec spec;
  bool notch_write = true;
  if (c.safe_mode == 1) {
    notch_write = false;
  } else if (c.safe_mode == 2) {
    spec.given = true;  // an explicit 0,0 is a profile with no insets, not "ask the table"
    spec.l_px = clamp_inset(c.safe_l);
    spec.r_px = clamp_inset(c.safe_r);
  }
  g_flag_observe = c.observe;
  g_ini_notch_write = notch_write;
  const bool cloud_path = c.cloud_on && !g_cloud_fallback;  // the fallback keeps the PC path even with cloud_mode=on
  g_ui_knob_mode = cloud_path ? 2 : 0;  // in cloud mode the game's own phone scale is the base; only the extra factor goes through the standard-resolution knob
  if (!g_cloud_seen || c.cloud_on != (g_cloud_mode != 0))
    Log("config: cloud_mode is %s", c.cloud_on ? "on (user platform = the client device, safe area from the device profile)" : "off");
  g_cloud_seen = true;
  g_cloud_mode = c.cloud_on ? 1 : 0;
  g_flag_notch_write = cloud_path ? false : notch_write;
  g_ui_rel = c.ui_rel;
  g_ui_off = c.ui_off;
  {
    char key[96];
    snprintf(key, sizeof(key), "%d/%.1f/%.1f/%d", c.safe_mode, spec.l_px, spec.r_px, g_flag_notch_write ? 1 : 0);
    if (g_safe_cfg_key != key) {
      if (!g_safe_cfg_key.empty()) {
        g_notch_reeval = true;
        g_force_profile = true;
        Log("config: safe_area changed (%s -> %s); applied once", g_safe_cfg_key.c_str(), key);
      }
      g_safe_cfg_key = key;
    }
  }
  g_spec_ini = spec;
  const float logged = (c.ui_off ? 50.0f : 0.0f) + c.safe_mode * 0.01f + spec.l_px * 0.001f + spec.r_px * 0.0001f + c.ui_rel * 1000.0f + (g_flag_notch_write ? 0.0f : 11.0f) + (c.observe ? 0.0f : 13.0f) +
                       (c.cloud_on ? 0.0f : 17.0f);
  if (g_ui_logged != logged) {
    Log("config (%ls): ui_scale=%s%.3f%s safe_area=%s cloud_mode=%s observe=%d notch_write=%d", g_ini_path.c_str(), c.ui_rel > 0 ? "" : "auto ", c.ui_rel, c.ui_off ? " (off)" : "",
        c.safe_mode == 0 ? "auto" : (c.safe_mode == 1 ? "off" : "pixels"), c.cloud_on ? "on" : "off", c.observe ? 1 : 0, g_flag_notch_write ? 1 : 0);
    g_ui_logged = logged;
  }
}

// the table row for a stream size: exact (either orientation) first, then the closest aspect within 1.5 %
const Dev *MatchTable(int w, int h, bool *exact) {
  const int W = w > h ? w : h, H = w > h ? h : w;
  *exact = false;
  for (const Dev &d : kDevices)
    if (d.w == W && d.h == H) {
      *exact = true;
      return &d;
    }
  const double aspect = static_cast<double>(W) / H;
  const Dev *best = nullptr;
  double best_score = 1e9;
  for (const Dev &d : kDevices) {
    const double da = std::fabs(static_cast<double>(d.w) / d.h - aspect) / aspect;
    if (da > 0.015 || d.w < 1600)  // small panels (iPhone SE) only match exactly: a 1920x1080 PC stream is not an SE
      continue;
    const double score = da * 100.0 + std::fabs(std::log(static_cast<double>(d.w) / W)) * 0.01;  // aspect first, then closeness in size
    if (score < best_score) {
      best_score = score;
      best = &d;
    }
  }
  return best;
}

Profile BuildProfile(const ProfileSpec &spec, int w, int h) {
  Profile p;
  if (spec.given) {
    p.source = "ini";
    p.name = "custom";
    p.cls = "phone";
    if (spec.l_px > 0 || spec.r_px > 0) {
      p.use_px = true;
      p.l_px = spec.l_px;
      p.r_px = spec.r_px;
    }
    return p;
  }
  bool exact = false;
  const Dev *dev = MatchTable(w, h, &exact);
  p.source = dev ? (exact ? "table" : "table (aspect match)") : "fallback";
  if (dev) {
    p.name = dev->key;
    p.cls = dev->cls;
    p.width_pt = dev->width_pt;
    p.l_pt = p.r_pt = dev->inset_pt;
    p.ui_scale = dev->ui_scale;
  } else {
    const int W = w > h ? w : h, H = w > h ? h : w;
    const double aspect = H > 0 ? static_cast<double>(W) / H : 0;
    p.name = "generic";
    p.cls = aspect >= 1.9 ? "phone" : (aspect <= 1.6 ? "pad" : "other");
  }
  return p;
}

// insets in screen pixels for a screen of w x h (landscape); the game insets the sides only
void ComputeInsets(const Profile &p, int w, int h, float *l, float *r, float *t, float *b) {
  *t = *b = 0;
  if (p.use_px) {
    *l = p.l_px;
    *r = p.r_px;
    return;
  }
  const int W = w > h ? w : h;
  const float scale = p.width_pt > 0 ? static_cast<float>(W) / p.width_pt : 0.0f;
  *l = p.l_pt * scale;
  *r = p.r_pt * scale;
}
bool ProfileHasInsets(const Profile &p) { return p.use_px ? (p.l_px > 0 || p.r_px > 0) : (p.width_pt > 0 && (p.l_pt > 0 || p.r_pt > 0)); }

// the profile for the current stream size, rebuilt when the size or the ini changes (and about every 5 s)
void EnsureProfile() {
  static LONG k = 0;
  ++k;
  FindModules();
  if (!ResolveScreen())
    return;
  const int w = g_screen_width(), h = g_screen_height();
  if (w <= 0 || h <= 0)
    return;
  if (!g_have_profile || w != g_last_w || h != g_last_h || k % 20 == 1 || g_force_profile) {
    g_force_profile = false;
    const Profile chosen = BuildProfile(g_spec_ini, w, h);
    if (!g_have_profile || !chosen.Same(g_profile) || w != g_last_w || h != g_last_h) {
      float l = 0, r = 0, t = 0, b = 0;
      ComputeInsets(chosen, w, h, &l, &r, &t, &b);
      const int W = w > h ? w : h, H = w > h ? h : w;
      Log("device profile: source=%s device=%s class=%s stream %d x %d (aspect %.3f); insets %.1f/%.1f pt (left/right) on a %.0f pt wide screen = %.0f/%.0f px", chosen.source.c_str(), chosen.name.c_str(),
          chosen.cls.c_str(), w, h, H > 0 ? static_cast<double>(W) / H : 0.0, chosen.use_px ? 0.0f : chosen.l_pt, chosen.use_px ? 0.0f : chosen.r_pt, chosen.width_pt, l, r);
      if (chosen.use_px)
        Log("device profile: the insets come from safe_area in endfield-ui.ini (stream pixels)");
      g_profile = chosen;
      g_have_profile = true;
      g_last_w = w;
      g_last_h = h;
    }
  }
}
