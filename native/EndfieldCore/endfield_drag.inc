// Drag threshold (included into endfield_runtime.cpp, inside its anonymous namespace, after endfield_il2cpp.inc).
//
// Unity UI turns a click into a drag when the finger moves more than EventSystem.pixelDragThreshold between down and up. The game keeps Unity's desktop value (10 px). On a phone stream
// (2868 x 1320 on an iPhone 17 Pro Max) ordinary finger jitter is more than that: the button shows its press (sound, effect) but the click never fires. On a native phone build the value
// is scaled with the screen's density. The Runtime sets it once per EventSystem object to 0.1 inch at the client's density (a table keyed by stream size; 1.5 % of the stream height for
// other sizes), never below the game's own value, and again only when the game resets it.
//
// endfield-ui.ini:  drag_px=auto (default) | <pixels 1-400> | off

using IntFn = int(__cdecl *)();
IntFn g_screen_width = nullptr, g_screen_height = nullptr;

// UnityEngine.Screen.width / height (icalls that only read two numbers)
bool ResolveScreen() {
  ResolveUnity();
  if (!g_screen_width && g_managed.resolve_icall) {
    g_screen_width = reinterpret_cast<IntFn>(g_managed.resolve_icall("UnityEngine.Screen::get_width()"));
    g_screen_height = reinterpret_cast<IntFn>(g_managed.resolve_icall("UnityEngine.Screen::get_height()"));
    if (!g_screen_width || !g_screen_height)
      g_screen_width = g_screen_height = nullptr;
  }
  return g_screen_width != nullptr;
}

struct DragApi {
  bool resolved = false, failed_logged = false;
  void *es_class = nullptr;
  const void *get_th = nullptr, *set_th = nullptr, *get_current = nullptr;
};
DragApi g_drag;
std::string g_drag_setting = "auto";  // auto | off | <pixels>
int g_drag_last_logged_target = -1;
std::unordered_map<void *, int> g_es_seen;  // EventSystem object -> the value it had after the last look
LONG g_es_resets = 0, g_drag_pumps = 0;

bool ResolveDrag() {
  if (g_drag.resolved)
    return true;
  FindModules();
  HMODULE game = g_managed.game_assembly;
  if (!game || !g_managed.runtime_invoke)
    return false;
  auto method_from_name = reinterpret_cast<MethodFromName>(GetProcAddress(game, "il2cpp_class_get_method_from_name"));
  if (!method_from_name || !ResolveFind())
    return false;  // not loaded yet
  g_drag.es_class = FindClassAnywhere("UnityEngine.EventSystems", "EventSystem");
  if (!g_drag.es_class)
    return false;
  g_drag.get_th = method_from_name(g_drag.es_class, "get_pixelDragThreshold", 0);
  g_drag.set_th = method_from_name(g_drag.es_class, "set_pixelDragThreshold", 1);
  g_drag.get_current = method_from_name(g_drag.es_class, "get_current", 0);
  Log("drag: resolved EventSystem=%p get/set pixelDragThreshold=%d/%d get_current=%d FindObjectsOfType(%d args)=found", g_drag.es_class, g_drag.get_th != nullptr, g_drag.set_th != nullptr,
      g_drag.get_current != nullptr, g_ui.find_args);
  if (!g_drag.get_th || !g_drag.set_th) {
    if (!g_drag.failed_logged) {
      g_drag.failed_logged = true;
      Log("drag: cannot set the threshold on this build (see the line above); nothing is changed");
    }
    return false;
  }
  g_drag.resolved = true;
  return true;
}

// pixel density of the client device, by stream size (landscape)
struct PpiRow { int w, h, ppi; };
const PpiRow kPpi[] = {{2868, 1320, 460}, {2622, 1206, 460}, {2736, 1260, 460}, {2556, 1179, 460}, {2796, 1290, 460}, {2532, 1170, 460}, {2778, 1284, 458}, {2340, 1080, 476},
                       {2436, 1125, 458}, {2688, 1242, 458}, {1792, 828, 326}, {1334, 750, 326}, {2266, 1488, 326}, {2360, 1640, 264}, {2388, 1668, 264}, {2420, 1668, 264},
                       {2732, 2048, 264}, {2752, 2064, 264}, {2160, 1620, 264}};

// the value to set for a stream of w x h; 0 = do not set
int ComputeDragTarget(const std::string &setting, int original, int w, int h, std::string *basis) {
  if (_stricmp(setting.c_str(), "off") == 0) {
    *basis = "drag_px=off";
    return 0;
  }
  char *end = nullptr;
  const double manual = strtod(setting.c_str(), &end);
  if (end != setting.c_str() && manual >= 1 && manual <= 400) {
    *basis = "drag_px from the ini";
    return static_cast<int>(manual + 0.5);
  }
  const int W = w > h ? w : h, H = w > h ? h : w;
  double px = 0;
  char text[160];
  for (const PpiRow &r : kPpi)
    if (r.w == W && r.h == H) {
      px = 0.1 * r.ppi;
      snprintf(text, sizeof(text), "auto: 0.1 inch at %d ppi (table, stream %d x %d)", r.ppi, W, H);
      *basis = text;
      break;
    }
  if (px == 0) {
    px = 0.015 * H;
    snprintf(text, sizeof(text), "auto: 1.5%% of the stream height %d (device not in the ppi table)", H);
    *basis = text;
  }
  if (px < 8) px = 8;
  if (px > 100) px = 100;
  int target = static_cast<int>(px + 0.5);
  if (target < original) {
    *basis += "; the game's own value is already larger, kept";
    target = original;
  }
  return target;
}

bool DragReadInt(const void *getter, void *object, int *out) { return ReadBoxed(getter, object, out); }

void EnsureDrag() {
  const LONG n = InterlockedIncrement(&g_drag_pumps);
  if (n != 1 && n % 4 != 1)
    return;  // once a second
  if (!ResolveDrag())
    return;
  ResolveScreen();
  void *current = nullptr;
  if (g_drag.get_current) {
    void *exception = nullptr;
    current = g_managed.runtime_invoke(g_drag.get_current, nullptr, nullptr, &exception);
    if (exception)
      current = nullptr;
  }
  // FindObjectsOfType(EventSystem, includeInactive) walks every MonoBehaviour of the scene: once at the start, every 30 s, and every 4 s while there is no current EventSystem; otherwise the one static read
  const bool full_scan = n == 1 || n % 120 == 1 || (!current && n % 16 == 1);
  void **systems = nullptr;
  void *single[1] = {current};
  size_t count = 0;
  if (full_scan) {
    count = FindObjects(g_drag.es_class, &systems);
  } else {
    systems = single;
    count = 1;
  }
  if (count == 0)
    return;
  const int w = g_screen_width ? g_screen_width() : 0, h = g_screen_height ? g_screen_height() : 0;
  std::unordered_map<void *, int> now;
  for (size_t i = 0; i < count; ++i) {
    void *es = systems[i];
    if (!es)
      continue;
    int value = 0;
    if (!DragReadInt(g_drag.get_th, es, &value))
      continue;
    auto it = g_es_seen.find(es);
    const bool known = it != g_es_seen.end();
    std::string basis;
    const int target = ComputeDragTarget(g_drag_setting, known ? it->second : value, w, h, &basis);
    if (!known)
      Log("drag: EventSystem %p%s: pixelDragThreshold ORIGINAL = %d (Unity's default is 10)", es, es == current ? " (the active one)" : "", value);
    if (target <= 0) {
      now[es] = value;
      if (!known)
        Log("drag: %s: not set", basis.c_str());
      continue;
    }
    if (target != g_drag_last_logged_target) {
      Log("drag: target %d px (%s)", target, basis.c_str());
      g_drag_last_logged_target = target;
    }
    int written = value;
    if (value != target) {
      int v = target;
      void *args[] = {&v};
      void *exception = nullptr;
      g_managed.runtime_invoke(g_drag.set_th, es, args, &exception);
      DragReadInt(g_drag.get_th, es, &written);
      if (!known)
        Log("drag: EventSystem %p: pixelDragThreshold %d -> %d (read back %d)%s", es, value, target, written, exception ? " [the setter raised an exception]" : "");
      else if (InterlockedIncrement(&g_es_resets) <= 40) {
        if (value != it->second)
          Log("drag: the GAME changed pixelDragThreshold on EventSystem %p to %d (we had set %d); set again to %d (read back %d)", es, value, it->second, target, written);
        else
          Log("drag: target changed (ini or stream size): EventSystem %p %d -> %d (read back %d)", es, value, target, written);
      }
    }
    now[es] = written;
  }
  if (full_scan)
    g_es_seen.swap(now);
  else
    for (auto &entry : now)
      g_es_seen[entry.first] = entry.second;
}
