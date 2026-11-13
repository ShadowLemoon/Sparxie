// UI size (included into endfield_runtime.cpp, inside its anonymous namespace, after endfield_ui_config.inc).
//
// The game's own UI scaling is Beyond.UI.UICanvasScaleHelper driving UnityEngine.UI.CanvasScaler (reference resolution, scale mode). On the PC build the helper multiplies the reference
// resolution by _customPCReferenceResolutionScale (UIConst.PC_REFERENCE_RESOLUTION_SCALE, 1.25), so the same UI is smaller than on a phone at the same pixel size.
// The Runtime writes only the helper's own inputs, once, and asks the helper to lay out again (its own ForceCanvasUpdate):
//  - knob A: _customPCReferenceResolutionScale = original / f;
//  - knob B: _useCustomStandardResolution + _customStandardResolution = standard resolution / f (cloud mode, or when A is verified to do nothing).
// f is ui_scale (relative to the PC default); auto = the device profile's size, else the phone build's (the PC multiplier removed). In cloud mode the game's own phone size is already the base,
// so only the factor above it is written. A value the game resets, or a new helper, gets the held value again; a changed ui_scale is applied once from the ORIGINAL values.

void ForceRelayout(const char *why) {
  if (!ResolveUi() || !g_ui.helper_class || !g_ui.m_h_force)
    return;
  void **helpers = nullptr;
  const size_t n = FindObjects(g_ui.helper_class, &helpers);
  for (size_t i = 0; i < n; ++i)
    if (helpers[i])
      Invoke0(g_ui.m_h_force, helpers[i], nullptr);
  Log("UI: UICanvasScaleHelper.ForceCanvasUpdate called on %zu helper(s) (%s)", n, why);
}

enum KnobPhase { kKnobWaiting, kKnobPcVerify, kKnobPcDone, kKnobStdVerify, kKnobStdDone, kKnobFailed, kKnobOff };
KnobPhase g_knob = kKnobWaiting;
float g_pc_orig = 1.0f, g_pc_target = 1.0f;
int g_pc_kind = 1;  // 1 float, 2 Vector2
float g_std_orig_res[2] = {0, 0};
unsigned char g_std_orig_use = 0;
float g_std_target[2] = {0, 0};
float g_f_used = 1.25f;
std::unordered_map<void *, Vec2> g_knob_before;
bool g_knob_before_valid = false;
Vec2 g_helper_std_before{0, 0};
float g_helper_scale_before = 0;
LONG g_knob_verify_at = 0, g_knob_resets = 0;
void *g_first_helper = nullptr;

bool HelperStd(void *helper, Vec2 *out, float *scale) {
  float sx = 0, sy = 0;
  const bool ok = ReadStd(g_ui.m_h_std_x, helper, &sx) && ReadStd(g_ui.m_h_std_y, helper, &sy);
  *out = {sx, sy};
  *scale = 0;
  if (g_ui.m_h_scale)
    ReadFloat(g_ui.m_h_scale, helper, scale);
  return ok;
}
void WritePc(void *helper, float v) {
  float two[2] = {v, v};
  g_ui.field_set(helper, g_ui.f_h_pc, two);
}
void ReadPc(void *helper, float *a) {
  float raw[2] = {1, 1};
  g_ui.field_get(helper, g_ui.f_h_pc, raw);
  *a = raw[0];
}
void WriteStd(void *helper, bool on) {
  unsigned char use = on ? 1 : 0;
  g_ui.field_set(helper, g_ui.f_h_use, &use);
  if (on)
    g_ui.field_set(helper, g_ui.f_h_res, g_std_target);
}
void CaptureBaseline() {
  g_knob_before.clear();
  void **scalers = nullptr;
  const size_t sn = FindObjects(g_ui.scaler_class, &scalers);
  for (size_t i = 0; i < sn; ++i) {
    int mode = -1;
    Vec2 ref{0, 0};
    ReadInt(g_ui.m_get_mode, scalers[i], &mode);
    if (mode == 1 && ReadVec2(g_ui.m_get_ref, scalers[i], &ref))
      g_knob_before[scalers[i]] = ref;
  }
  g_knob_before_valid = !g_knob_before.empty();
}
// true when the game moved: a helper getter, or a scaler we saw before
bool KnobEffective(void *helper, char *why, size_t cap) {
  Vec2 std_now{0, 0};
  float scale_now = 0;
  if (HelperStd(helper, &std_now, &scale_now) && (std::fabs(std_now.x - g_helper_std_before.x) > 1.0f || std::fabs(std_now.y - g_helper_std_before.y) > 1.0f || std::fabs(scale_now - g_helper_scale_before) > 0.001f)) {
    snprintf(why, cap, "helper standard resolution (%.0f x %.0f, scale %.3f) -> (%.0f x %.0f, scale %.3f)", g_helper_std_before.x, g_helper_std_before.y, g_helper_scale_before, std_now.x, std_now.y, scale_now);
    return true;
  }
  void **scalers = nullptr;
  const size_t sn = FindObjects(g_ui.scaler_class, &scalers);
  for (size_t i = 0; i < sn; ++i) {
    auto it = g_knob_before.find(scalers[i]);
    if (it == g_knob_before.end())
      continue;
    Vec2 now{0, 0};
    if (ReadVec2(g_ui.m_get_ref, scalers[i], &now) && std::fabs(now.x - it->second.x) > it->second.x * 0.01f) {
      snprintf(why, cap, "CanvasScaler reference (%.0f x %.0f) -> (%.0f x %.0f)", it->second.x, it->second.y, now.x, now.y);
      return true;
    }
  }
  return false;
}

// what the UI looks like now: every CanvasScaler with its reference resolution and its canvas scale, every helper with its custom resolution; for the log, a few times per session
LONG g_state_logs = 0, g_state_due = 0;
void LogUiState(const char *when) {
  if (!g_ui.scaler_class || InterlockedIncrement(&g_state_logs) > 14)
    return;
  void **scalers = nullptr;
  const size_t n = FindObjects(g_ui.scaler_class, &scalers);
  Log("UI state (%s): %zu CanvasScaler(s), factor x%.3f", when, n, g_f_used);
  size_t shown = 0;
  for (size_t i = 0; i < n && shown < 16; ++i) {
    int mode = -1;
    Vec2 ref{0, 0};
    ReadInt(g_ui.m_get_mode, scalers[i], &mode);
    ReadVec2(g_ui.m_get_ref, scalers[i], &ref);
    if (mode != 1)
      continue;
    void *canvas = nullptr;
    if (g_ui.f_scaler_canvas)
      g_ui.field_get(scalers[i], g_ui.f_scaler_canvas, &canvas);
    float csf = 0;
    if (canvas && g_ui.m_c_get_sf)
      ReadFloat(g_ui.m_c_get_sf, canvas, &csf);
    char name[96];
    Log("UI state: scaler '%s' reference %.0f x %.0f canvas scale %.3f", NameOf(scalers[i], name, sizeof(name)), ref.x, ref.y, csf);
    ++shown;
  }
  void **helpers = nullptr;
  const size_t hn = g_ui.helper_class ? FindObjects(g_ui.helper_class, &helpers) : 0;
  for (size_t i = 0; i < hn && i < 10; ++i) {
    unsigned char use = 0;
    Vec2 cur{0, 0}, sv{0, 0};
    float sc = 0;
    g_ui.field_get(helpers[i], g_ui.f_h_use, &use);
    g_ui.field_get(helpers[i], g_ui.f_h_res, &cur);
    HelperStd(helpers[i], &sv, &sc);
    Log("UI state: helper %p use=%u custom (%.0f x %.0f) standard getters (%.0f x %.0f) scale %.3f", helpers[i], use, cur.x, cur.y, sv.x, sv.y, sc);
  }
}

// the factor the ini asks for: off = 1.0 (the game's own size), a number = that, auto = the PC multiplier removed (the phone build's size; 1.25 when the game value is not readable)
float WantedFactor(float pc_orig) {
  // the factor relative to the PC default; in cloud mode the base is the game's own phone size, which already is PC default x PC multiplier: the extra factor is what is left
  const float base = g_cloud_active ? g_cloud_pc_scale : 1.0f;
  if (g_ui_off)
    return 1.0f;
  float total;
  if (g_ui_rel > 0)
    total = g_ui_rel;
  else if (g_have_profile && g_profile.ui_scale > 0)
    total = g_profile.ui_scale;
  else
    total = pc_orig > 1.001f ? pc_orig : 1.25f;
  return g_cloud_active ? total / base : total;
}

void EnsureUiKnob() {
  if (g_knob == kKnobOff || g_knob == kKnobFailed)
    return;
  if (g_ui_off && g_knob == kKnobWaiting)
    return;  // ui_scale=off from the start: nothing is touched
  if (g_cloud_mode && !g_cloud_fallback && (!g_cloud_active || g_pumps < g_cloud_since + 12))
    return;  // cloud mode: the base size is the game's phone size; measured only after the slots were filled and the UI laid out again
  if (g_knob == kKnobWaiting && g_ui_rel <= 0 && !g_have_profile)
    return;  // ui_scale=auto depends on the device profile: not decided before it is known (no second jump)
  if (g_state_due && g_pumps >= g_state_due) {
    g_state_due = 0;
    FindModules();
    if (ResolveUi())
      LogUiState("settled");
  }
  FindModules();
  if (!ResolveUi())
    return;
  if (!g_ui.helper_class || !g_ui.f_h_pc) {
    g_knob = kKnobFailed;
    Log("UI size: UICanvasScaleHelper or its _customPCReferenceResolutionScale field is missing; nothing is changed");
    return;
  }
  const bool settled = g_knob == kKnobPcDone || g_knob == kKnobStdDone;
  if (g_knob == kKnobWaiting ? false : (settled ? (g_pumps % 4 != 1) : false))
    return;
  void **helpers = nullptr;
  const size_t hn = FindObjects(g_ui.helper_class, &helpers);
  if (hn == 0)
    return;  // the UI is not built yet: looked at again on the next pump
  void *first = helpers[0];

  if (g_knob == kKnobWaiting) {
    CaptureBaseline();
    float pc = 1.0f;
    ReadPc(first, &pc);
    const void *ftype = g_refl.ok || ResolveRefl() ? g_refl.field_type(g_ui.f_h_pc) : nullptr;
    g_pc_kind = (TypeKind(ftype) == 17) ? 2 : 1;
    g_pc_orig = pc;
    HelperStd(first, &g_helper_std_before, &g_helper_scale_before);
    unsigned char use = 0;
    g_ui.field_get(first, g_ui.f_h_use, &use);
    Vec2 cur{0, 0};
    g_ui.field_get(first, g_ui.f_h_res, &cur);
    g_std_orig_use = use;
    g_std_orig_res[0] = cur.x;
    g_std_orig_res[1] = cur.y;
    Log("UI size: ORIGINAL values: _customPCReferenceResolutionScale=%.4f (field type %s), _useCustomStandardResolution=%u, _customStandardResolution=(%.1f, %.1f), standard resolution getters = (%.0f x %.0f), standardResolutionScale=%.4f; %zu helper(s), %zu mode-1 CanvasScaler(s) seen",
        pc, g_pc_kind == 2 ? "Vector2" : "float", use, cur.x, cur.y, g_helper_std_before.x, g_helper_std_before.y, g_helper_scale_before, hn, g_knob_before.size());
    if (g_knob_before_valid) {
      size_t shown = 0;
      for (auto &kv : g_knob_before) {
        char name[96];
        Log("UI size: before: CanvasScaler %p '%s' reference (%.0f x %.0f)", kv.first, NameOf(kv.first, name, sizeof(name)), kv.second.x, kv.second.y);
        if (++shown >= 6)
          break;
      }
    }
    // f: how much bigger than the PC default the UI becomes. Auto = the PC reference scale itself (the phone value); ui_scale=<f> is relative to the PC default.
    g_f_used = WantedFactor(pc);
    if (g_ui_knob_mode == 2 || (g_ui_knob_mode == 0 && !(pc > 1.001f))) {
      if (g_ui_knob_mode == 0)
        Log("UI size: the PC reference scale is already <= 1.0; going straight to the standard-resolution knob");
      goto try_std;
    }
    g_pc_target = pc / g_f_used;
    for (size_t i = 0; i < hn; ++i)
      if (helpers[i])
        WritePc(helpers[i], g_pc_target);
    Log("UI size: SET once: _customPCReferenceResolutionScale %.4f -> %.4f (UI x%.3f relative to the PC default); now the helper's own ForceCanvasUpdate", pc, g_pc_target, g_f_used);
    for (size_t i = 0; i < hn; ++i)
      if (helpers[i] && g_ui.m_h_force)
        Invoke0(g_ui.m_h_force, helpers[i], nullptr);
    g_knob = kKnobPcVerify;
    g_knob_verify_at = g_pumps + 8;
    g_first_helper = first;
    return;
  try_std:
    {
      Vec2 std_now = g_helper_std_before;
      if (std_now.x <= 0 || std_now.y <= 0)
        std_now = {1920.0f, 1080.0f};
      g_std_target[0] = std_now.x / g_f_used;
      g_std_target[1] = std_now.y / g_f_used;
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i])
          WriteStd(helpers[i], true);
      Log("UI size: SET once (knob B): custom standard resolution (%.0f x %.0f) -> (%.0f x %.0f), use=1 (UI x%.3f); now the helper's own ForceCanvasUpdate", std_now.x, std_now.y, g_std_target[0], g_std_target[1], g_f_used);
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i] && g_ui.m_h_force)
          Invoke0(g_ui.m_h_force, helpers[i], nullptr);
      g_knob = kKnobStdVerify;
      g_knob_verify_at = g_pumps + 8;
      g_first_helper = first;
    }
    return;
  }

  if (g_knob == kKnobPcVerify || g_knob == kKnobStdVerify) {
    if (g_pumps < g_knob_verify_at)
      return;
    char why[200] = {};
    const bool pc_phase = g_knob == kKnobPcVerify;
    if (KnobEffective(first, why, sizeof(why))) {
      Log("UI size: %s EFFECTIVE: %s", pc_phase ? "knob A (PC reference scale)" : "knob B (custom standard resolution)", why);
      g_knob = pc_phase ? kKnobPcDone : kKnobStdDone;
      g_notch_reeval = true;  // the canvas width changed: the notch value is worked out from it again
      LogUiState("after the size was applied");
      g_state_due = static_cast<LONG>(g_pumps) + 80;
      return;
    }
    if (!g_knob_before_valid) {
      Log("UI size: %s set, effect NOT verifiable (no mode-1 CanvasScaler existed before and the helper getters did not move); the value stays", pc_phase ? "knob A" : "knob B");
      g_knob = pc_phase ? kKnobPcDone : kKnobStdDone;
      return;
    }
    if (pc_phase) {
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i])
          WritePc(helpers[i], g_pc_orig);
      Log("UI size: knob A did nothing measurable; _customPCReferenceResolutionScale put back to %.4f; trying knob B", g_pc_orig);
      Vec2 std_now = g_helper_std_before;
      if (std_now.x <= 0 || std_now.y <= 0)
        std_now = {1920.0f, 1080.0f};
      g_std_target[0] = std_now.x / g_f_used;
      g_std_target[1] = std_now.y / g_f_used;
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i])
          WriteStd(helpers[i], true);
      Log("UI size: SET once (knob B): custom standard resolution -> (%.0f x %.0f), use=1", g_std_target[0], g_std_target[1]);
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i] && g_ui.m_h_force)
          Invoke0(g_ui.m_h_force, helpers[i], nullptr);
      g_knob = kKnobStdVerify;
      g_knob_verify_at = g_pumps + 8;
      return;
    }
    for (size_t i = 0; i < hn; ++i)
      if (helpers[i]) {
        if (pc_phase)
          WritePc(helpers[i], g_pc_orig);
        else {
          unsigned char use = g_std_orig_use;
          g_ui.field_set(helpers[i], g_ui.f_h_use, &use);
          g_ui.field_set(helpers[i], g_ui.f_h_res, g_std_orig_res);
        }
      }
    Log("UI size: %s did nothing measurable; the original values were put back. No UI size knob worked on this build.", pc_phase ? "knob A" : "knob B");
    g_knob = kKnobFailed;
    return;
  }

  // settled: a changed ui_scale in the ini is applied once (from the ORIGINAL values, not from the last written ones) ...
  const bool pc_knob = g_knob == kKnobPcDone;
  {
    const float want = WantedFactor(g_pc_orig);
    if (std::fabs(want - g_f_used) > 0.001f) {
      const float before = g_f_used;
      g_f_used = want;
      if (pc_knob) {
        g_pc_target = g_pc_orig / g_f_used;
        for (size_t i = 0; i < hn; ++i)
          if (helpers[i])
            WritePc(helpers[i], g_pc_target);
      } else {
        Vec2 std_now = g_helper_std_before;
        if (std_now.x <= 0 || std_now.y <= 0)
          std_now = {1920.0f, 1080.0f};
        g_std_target[0] = std_now.x / g_f_used;
        g_std_target[1] = std_now.y / g_f_used;
        for (size_t i = 0; i < hn; ++i)
          if (helpers[i])
            WriteStd(helpers[i], true);
      }
      Log("UI size: ui_scale changed live: x%.3f -> x%.3f relative to the PC default (%s %.4f); the helper's own ForceCanvasUpdate, once", before, g_f_used, pc_knob ? "pc reference scale" : "custom standard resolution",
          pc_knob ? g_pc_target : g_std_target[0]);
      for (size_t i = 0; i < hn; ++i)
        if (helpers[i] && g_ui.m_h_force)
          Invoke0(g_ui.m_h_force, helpers[i], nullptr);
      g_notch_reeval = true;
      g_state_due = static_cast<LONG>(g_pumps) + 8;
      return;
    }
  }
  // ... and only if the game resets the one field, or a new helper object appears, is it applied again (and logged)
  for (size_t i = 0; i < hn; ++i) {
    void *h = helpers[i];
    if (!h)
      continue;
    bool reset = false;
    if (pc_knob) {
      float v = 0;
      ReadPc(h, &v);
      reset = std::fabs(v - g_pc_target) > 0.001f;
      if (reset) {
        const bool first_seen = h != first || g_knob_resets > 0;
        if (InterlockedIncrement(&g_knob_resets) <= 40)
          Log("UI size: %s: _customPCReferenceResolutionScale was %.4f, set again to %.4f", first_seen ? "the game (or a new helper) reset it" : "reset", v, g_pc_target);
        WritePc(h, g_pc_target);
      }
    } else {
      unsigned char use = 0;
      Vec2 cur{0, 0};
      g_ui.field_get(h, g_ui.f_h_use, &use);
      g_ui.field_get(h, g_ui.f_h_res, &cur);
      reset = use != 1 || !Near(cur.x, g_std_target[0]) || !Near(cur.y, g_std_target[1]);
      if (reset) {
        if (InterlockedIncrement(&g_knob_resets) <= 40)
          Log("UI size: the game (or a new helper) reset the custom standard resolution (use=%u, %.0f x %.0f); set again to (%.0f x %.0f)", use, cur.x, cur.y, g_std_target[0], g_std_target[1]);
        WriteStd(h, true);
      }
    }
    if (reset && g_ui.m_h_force)
      Invoke0(g_ui.m_h_force, h, nullptr);
  }
}
