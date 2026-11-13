// IL2CPP reflection helpers (included into endfield_runtime.cpp, inside its anonymous namespace, before the parts that use them).
// Everything goes through the exports of GameAssembly.dll; every call runs on the game's main thread (from the pump or the message hook).

struct Vec2 { float x, y; };
using ClassGetType = const void *(__cdecl *)(void *);
using TypeGetObject = void *(__cdecl *)(const void *);
using ArrayLength = size_t(__cdecl *)(void *);
using ClassGetMethods = const void *(__cdecl *)(void *, void **);
using MethodGetName = const char *(__cdecl *)(const void *);
using MethodGetParamCount = unsigned(__cdecl *)(const void *);
using MethodIsGeneric = bool(__cdecl *)(const void *);
using ClassGetField = void *(__cdecl *)(void *, const char *);
using FieldGetValue = void(__cdecl *)(void *, void *, void *);
using FieldSetValue = void(__cdecl *)(void *, void *, void *);
using DomainAssemblies2 = const void **(__cdecl *)(void *, size_t *);
using MethodGetReturnType = const void *(__cdecl *)(const void *);
using TypeGetType = int(__cdecl *)(const void *);

// the Unity UI classes and members the Runtime reads or writes
struct UiLever {
  bool resolved = false;
  bool failed_logged = false;
  void *scaler_class = nullptr;
  void *helper_class = nullptr;
  const void *find_objects = nullptr;
  int find_args = 0;
  const void *m_get_ref = nullptr, *m_set_ref = nullptr, *m_get_mode = nullptr, *m_get_sf = nullptr;
  const void *m_h_std_x = nullptr, *m_h_std_y = nullptr, *m_h_force = nullptr;
  void *f_h_use = nullptr, *f_h_res = nullptr, *f_h_pc = nullptr;
  const void *m_h_scale = nullptr;
  TypeGetObject type_get_object = nullptr;
  ClassGetType class_get_type = nullptr;
  ArrayLength array_length = nullptr;
  FieldGetValue field_get = nullptr;
  FieldSetValue field_set = nullptr;
  MethodGetReturnType ret_type = nullptr;
  TypeGetType type_type = nullptr;
  void *canvas_class = nullptr;
  const void *m_c_root = nullptr, *m_c_mode = nullptr, *m_c_get_sf = nullptr, *m_name = nullptr;
  void *f_scaler_canvas = nullptr;
};
UiLever g_ui;

void *FindClassAnywhere(const char *ns, const char *name) {
  HMODULE game = g_managed.game_assembly;
  auto domain_get = reinterpret_cast<DomainGet>(GetProcAddress(game, "il2cpp_domain_get"));
  auto assembly_image = reinterpret_cast<AssemblyImage>(GetProcAddress(game, "il2cpp_assembly_get_image"));
  auto class_from_name = reinterpret_cast<ClassFromName>(GetProcAddress(game, "il2cpp_class_from_name"));
  auto domain_assemblies = reinterpret_cast<DomainAssemblies2>(GetProcAddress(game, "il2cpp_domain_get_assemblies"));
  if (!domain_get || !assembly_image || !class_from_name || !domain_assemblies)
    return nullptr;
  size_t count = 0;
  const void **list = domain_assemblies(domain_get(), &count);
  for (size_t i = 0; list && i < count && i < 4096; ++i) {
    const void *image = list[i] ? assembly_image(list[i]) : nullptr;
    void *klass = image ? class_from_name(image, ns, name) : nullptr;
    if (klass)
      return klass;
  }
  return nullptr;
}

// UnityEngine.Object.FindObjectsOfType and what reading its result needs: prefer (Type, bool includeInactive), then (Type)
bool ResolveFind() {
  if (g_ui.find_objects)
    return true;
  HMODULE game = g_managed.game_assembly;
  if (!game || !g_managed.runtime_invoke)
    return false;
  auto get_methods = reinterpret_cast<ClassGetMethods>(GetProcAddress(game, "il2cpp_class_get_methods"));
  auto method_name = reinterpret_cast<MethodGetName>(GetProcAddress(game, "il2cpp_method_get_name"));
  auto param_count = reinterpret_cast<MethodGetParamCount>(GetProcAddress(game, "il2cpp_method_get_param_count"));
  auto is_generic = reinterpret_cast<MethodIsGeneric>(GetProcAddress(game, "il2cpp_method_is_generic"));
  g_ui.class_get_type = reinterpret_cast<ClassGetType>(GetProcAddress(game, "il2cpp_class_get_type"));
  g_ui.type_get_object = reinterpret_cast<TypeGetObject>(GetProcAddress(game, "il2cpp_type_get_object"));
  g_ui.array_length = reinterpret_cast<ArrayLength>(GetProcAddress(game, "il2cpp_array_length"));
  if (!get_methods || !method_name || !param_count || !is_generic || !g_ui.class_get_type || !g_ui.type_get_object || !g_ui.array_length)
    return false;
  void *object_class = FindClassAnywhere("UnityEngine", "Object");
  if (!object_class)
    return false;  // not loaded yet
  for (int want : {2, 1}) {
    void *iter = nullptr;
    while (const void *m = get_methods(object_class, &iter)) {
      const char *n = method_name(m);
      if (n && strcmp(n, "FindObjectsOfType") == 0 && !is_generic(m) && static_cast<int>(param_count(m)) == want) {
        g_ui.find_objects = m;
        g_ui.find_args = want;
        break;
      }
    }
    if (g_ui.find_objects)
      break;
  }
  return g_ui.find_objects != nullptr;
}

// objects of one class, as raw managed pointers (0 on failure)
size_t FindObjects(void *klass, void ***out_elements) {
  *out_elements = nullptr;
  if (!klass || !ResolveFind())
    return 0;
  const void *type = g_ui.class_get_type(klass);
  void *type_object = type ? g_ui.type_get_object(type) : nullptr;
  if (!type_object)
    return 0;
  bool include_inactive = true;
  void *args[2] = {type_object, &include_inactive};
  void *exception = nullptr;
  void *array = g_managed.runtime_invoke(g_ui.find_objects, nullptr, args, &exception);
  if (exception || !array)
    return 0;
  const size_t length = g_ui.array_length(array);
  if (length > 100000)
    return 0;
  *out_elements = reinterpret_cast<void **>(static_cast<char *>(array) + 0x20);  // Il2CppArray: object header 16, bounds 8, max_length 8, then the elements
  return length;
}

bool Invoke0(const void *method, void *object, void **result_boxed) {
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(method, object, nullptr, &exception);
  if (result_boxed)
    *result_boxed = boxed;
  return !exception;
}
const void *Unboxed(void *boxed) {
  if (!boxed)
    return nullptr;
  return g_managed.object_unbox ? g_managed.object_unbox(boxed) : static_cast<const char *>(boxed) + 2 * sizeof(void *);
}
template <typename T>
bool ReadBoxed(const void *getter, void *object, T *out) {
  void *boxed = nullptr;
  if (!getter || !Invoke0(getter, object, &boxed))
    return false;
  const T *v = static_cast<const T *>(Unboxed(boxed));
  if (!v)
    return false;
  *out = *v;
  return true;
}
// ---- the Unity UI classes (CanvasScaler, Canvas, the game's UICanvasScaleHelper)
bool ResolveUi() {
  if (g_ui.resolved)
    return true;
  HMODULE game = g_managed.game_assembly;
  if (!game || !g_managed.runtime_invoke)
    return false;
  auto method_from_name = reinterpret_cast<MethodFromName>(GetProcAddress(game, "il2cpp_class_get_method_from_name"));
  auto get_field = reinterpret_cast<ClassGetField>(GetProcAddress(game, "il2cpp_class_get_field_from_name"));
  g_ui.field_get = reinterpret_cast<FieldGetValue>(GetProcAddress(game, "il2cpp_field_get_value"));
  g_ui.field_set = reinterpret_cast<FieldSetValue>(GetProcAddress(game, "il2cpp_field_set_value"));
  g_ui.ret_type = reinterpret_cast<MethodGetReturnType>(GetProcAddress(game, "il2cpp_method_get_return_type"));
  g_ui.type_type = reinterpret_cast<TypeGetType>(GetProcAddress(game, "il2cpp_type_get_type"));
  if (!method_from_name || !get_field || !g_ui.field_get || !g_ui.field_set) {
    if (!g_ui.failed_logged) {
      g_ui.failed_logged = true;
      Log("UI scale: needed il2cpp exports are missing (method_from_name=%d field_from_name=%d field_get=%d field_set=%d)", method_from_name != nullptr, get_field != nullptr,
          g_ui.field_get != nullptr, g_ui.field_set != nullptr);
    }
    return false;
  }
  if (!ResolveFind())
    return false;
  g_ui.scaler_class = FindClassAnywhere("UnityEngine.UI", "CanvasScaler");
  void *object_class = FindClassAnywhere("UnityEngine", "Object");
  if (!g_ui.scaler_class || !object_class) {
    static bool noted = false;
    if (!noted) {
      noted = true;
      Log("UI scale: classes not loaded yet (CanvasScaler=%p UnityEngine.Object=%p); trying again", g_ui.scaler_class, object_class);
    }
    return false;
  }
  g_ui.helper_class = FindClassAnywhere("Beyond.UI", "UICanvasScaleHelper");
  g_ui.m_get_ref = method_from_name(g_ui.scaler_class, "get_referenceResolution", 0);
  g_ui.m_set_ref = method_from_name(g_ui.scaler_class, "set_referenceResolution", 1);
  g_ui.m_get_mode = method_from_name(g_ui.scaler_class, "get_uiScaleMode", 0);
  g_ui.m_get_sf = method_from_name(g_ui.scaler_class, "get_scaleFactor", 0);
  g_ui.f_scaler_canvas = get_field(g_ui.scaler_class, "m_Canvas");
  g_ui.canvas_class = FindClassAnywhere("UnityEngine", "Canvas");
  if (g_ui.canvas_class) {
    g_ui.m_c_root = method_from_name(g_ui.canvas_class, "get_isRootCanvas", 0);
    g_ui.m_c_mode = method_from_name(g_ui.canvas_class, "get_renderMode", 0);
    g_ui.m_c_get_sf = method_from_name(g_ui.canvas_class, "get_scaleFactor", 0);
  }
  g_ui.m_name = method_from_name(object_class, "get_name", 0);
  if (g_ui.helper_class) {
    g_ui.m_h_std_x = method_from_name(g_ui.helper_class, "get_standardHorizontalResolution", 0);
    g_ui.m_h_std_y = method_from_name(g_ui.helper_class, "get_standardVerticalResolution", 0);
    g_ui.m_h_force = method_from_name(g_ui.helper_class, "ForceCanvasUpdate", 0);
    g_ui.m_h_scale = method_from_name(g_ui.helper_class, "get_standardResolutionScale", 0);
    g_ui.f_h_use = get_field(g_ui.helper_class, "_useCustomStandardResolution");
    g_ui.f_h_res = get_field(g_ui.helper_class, "_customStandardResolution");
    g_ui.f_h_pc = get_field(g_ui.helper_class, "_customPCReferenceResolutionScale");
  }
  Log("UI scale: resolved CanvasScaler=%p helper=%p Canvas=%p FindObjectsOfType(%d args)=%s get/set referenceResolution=%d/%d scaleFactor=%d uiScaleMode=%d helper getters=%d/%d fields=%d/%d/%d ForceCanvasUpdate=%d",
      g_ui.scaler_class, g_ui.helper_class, g_ui.canvas_class, g_ui.find_args, g_ui.find_objects ? "found" : "MISSING", g_ui.m_get_ref != nullptr, g_ui.m_set_ref != nullptr, g_ui.m_get_sf != nullptr,
      g_ui.m_get_mode != nullptr, g_ui.m_h_std_x != nullptr, g_ui.m_h_std_y != nullptr, g_ui.f_h_use != nullptr, g_ui.f_h_res != nullptr, g_ui.f_h_pc != nullptr, g_ui.m_h_force != nullptr);
  if (!g_ui.m_get_ref || !g_ui.m_set_ref)
    return false;
  g_ui.resolved = true;
  return true;
}

bool ReadVec2(const void *getter, void *object, Vec2 *out) { return ReadBoxed(getter, object, out); }
bool ReadFloat(const void *getter, void *object, float *out) { return ReadBoxed(getter, object, out); }
bool ReadInt(const void *getter, void *object, int *out) { return ReadBoxed(getter, object, out); }
bool ReadBool(const void *getter, void *object, int *out) {
  unsigned char v = 0;
  if (!ReadBoxed(getter, object, &v))
    return false;
  *out = v ? 1 : 0;
  return true;
}
bool Near(float a, float b) { return a > b - 0.01f && a < b + 0.01f; }

// a standard-resolution getter that returns int or float
bool ReadStd(const void *getter, void *object, float *out) {
  if (!getter)
    return false;
  const void *rt = g_ui.ret_type ? g_ui.ret_type(getter) : nullptr;
  const int kind = (rt && g_ui.type_type) ? g_ui.type_type(rt) : 0;  // IL2CPP_TYPE_I4 = 8, IL2CPP_TYPE_R4 = 12
  if (kind == 12)
    return ReadFloat(getter, object, out);
  int value = 0;
  if (!ReadInt(getter, object, &value))
    return false;
  *out = static_cast<float>(value);
  return true;
}

// UTF-8 text of a System.String (length at +0x10, UTF-16 characters from +0x14)
std::string Utf8Of(void *str) {
  if (!str)
    return "";
  const int length = *reinterpret_cast<int *>(static_cast<char *>(str) + 0x10);
  const wchar_t *chars = reinterpret_cast<wchar_t *>(static_cast<char *>(str) + 0x14);
  if (length <= 0 || length > 200)
    return "";
  char out[256] = {};
  const int n = WideCharToMultiByte(CP_UTF8, 0, chars, length, out, sizeof(out) - 1, nullptr, nullptr);
  return std::string(out, n > 0 ? n : 0);
}

// UnityEngine.Object.name, for the log
const char *NameOf(void *object, char *out, size_t cap) {
  out[0] = 0;
  if (!object || !g_ui.m_name)
    return out;
  void *exception = nullptr;
  void *str = g_managed.runtime_invoke(g_ui.m_name, object, nullptr, &exception);
  if (exception || !str)
    return out;
  snprintf(out, cap, "%s", Utf8Of(str).c_str());
  return out;
}

// ---- signatures, numeric calls, static fields
using TypeGetName = char *(__cdecl *)(const void *);
using MethodGetParam = const void *(__cdecl *)(const void *, unsigned);
using MethodGetParamName = const char *(__cdecl *)(const void *, unsigned);
using MethodIsInstance = bool(__cdecl *)(const void *);
using FieldGetType2 = const void *(__cdecl *)(void *);
using FieldStaticGet = void(__cdecl *)(void *, void *);
struct Refl {
  TypeGetName type_name = nullptr;
  MethodGetParam get_param = nullptr;
  MethodGetParamName param_name = nullptr;
  MethodIsInstance is_instance = nullptr;
  FieldGetType2 field_type = nullptr;
  FieldStaticGet static_get = nullptr;
  ClassGetMethods get_methods = nullptr;
  MethodGetName method_name = nullptr;
  MethodGetParamCount param_count = nullptr;
  ClassGetField get_field = nullptr;
  bool ok = false;
};
Refl g_refl;
bool ResolveRefl() {
  if (g_refl.ok)
    return true;
  HMODULE game = g_managed.game_assembly;
  if (!game)
    return false;
  g_refl.type_name = reinterpret_cast<TypeGetName>(GetProcAddress(game, "il2cpp_type_get_name"));
  g_refl.get_param = reinterpret_cast<MethodGetParam>(GetProcAddress(game, "il2cpp_method_get_param"));
  g_refl.param_name = reinterpret_cast<MethodGetParamName>(GetProcAddress(game, "il2cpp_method_get_param_name"));
  g_refl.is_instance = reinterpret_cast<MethodIsInstance>(GetProcAddress(game, "il2cpp_method_is_instance"));
  g_refl.field_type = reinterpret_cast<FieldGetType2>(GetProcAddress(game, "il2cpp_field_get_type"));
  g_refl.static_get = reinterpret_cast<FieldStaticGet>(GetProcAddress(game, "il2cpp_field_static_get_value"));
  g_refl.get_methods = reinterpret_cast<ClassGetMethods>(GetProcAddress(game, "il2cpp_class_get_methods"));
  g_refl.method_name = reinterpret_cast<MethodGetName>(GetProcAddress(game, "il2cpp_method_get_name"));
  g_refl.param_count = reinterpret_cast<MethodGetParamCount>(GetProcAddress(game, "il2cpp_method_get_param_count"));
  g_refl.get_field = reinterpret_cast<ClassGetField>(GetProcAddress(game, "il2cpp_class_get_field_from_name"));
  g_refl.ok = g_refl.type_name && g_refl.get_param && g_refl.is_instance && g_refl.field_type && g_refl.static_get && g_refl.get_methods && g_refl.method_name && g_refl.param_count && g_refl.get_field &&
              g_ui.ret_type && g_ui.type_type;
  return g_refl.ok;
}
int TypeKind(const void *type) { return (type && g_ui.type_type) ? g_ui.type_type(type) : 0; }  // BOOLEAN 2, I1..U2 4-7, I4 8, U4 9, I8 10, U8 11, R4 12, R8 13, STRING 14, VOID 1
bool NumericKind(int k) { return k == 2 || (k >= 4 && k <= 13); }
std::string Sig(const void *m) {
  std::string out = std::string(g_refl.is_instance(m) ? "" : "static ");
  const void *rt = g_ui.ret_type(m);
  char *rn = rt ? g_refl.type_name(rt) : nullptr;
  out += (rn ? rn : "?");
  out += " ";
  out += g_refl.method_name(m);
  out += "(";
  const unsigned n = g_refl.param_count(m);
  for (unsigned i = 0; i < n; ++i) {
    const void *pt = g_refl.get_param(m, i);
    char *pn = pt ? g_refl.type_name(pt) : nullptr;
    const char *nm = g_refl.param_name ? g_refl.param_name(m, i) : nullptr;
    if (i) out += ", ";
    out += (pn ? pn : "?");
    out += " ";
    out += (nm ? nm : "");
  }
  out += ")";
  return out;
}
// every method called `name` of a class, all arities
std::vector<const void *> MethodsNamed(void *klass, const char *name) {
  std::vector<const void *> found;
  void *iter = nullptr;
  while (const void *m = g_refl.get_methods(klass, &iter)) {
    const char *n = g_refl.method_name(m);
    if (n && strcmp(n, name) == 0)
      found.push_back(m);
  }
  return found;
}
void LogMethods(void *klass, const char *class_name, const char *name) {
  auto ms = MethodsNamed(klass, name);
  if (ms.empty()) {
    Log("signature: %s.%s: not found", class_name, name);
    return;
  }
  for (const void *m : ms)
    Log("signature: %s.%s", class_name, Sig(m).c_str());
}
union Slot { int32_t i; uint32_t u; int64_t l; float f; double d; unsigned char b; };
// calls a static (or instance, with object) method whose parameters are all numeric/bool; missing arguments are 0/false. false when the shape is not supported.
bool CallNumeric(const void *method, void *object, const std::vector<double> &in, double *out, bool *has_result) {
  if (!method)
    return false;
  const unsigned n = g_refl.param_count(method);
  if (n > 6)
    return false;
  Slot slots[6] = {};
  void *args[6] = {};
  for (unsigned i = 0; i < n; ++i) {
    const void *pt = g_refl.get_param(method, i);
    const int k = TypeKind(pt);
    if (!NumericKind(k))
      return false;
    const double v = i < in.size() ? in[i] : 0.0;
    switch (k) {
      case 2: slots[i].b = v != 0.0; break;
      case 4: case 5: slots[i].b = static_cast<unsigned char>(static_cast<int>(v)); break;
      case 6: case 7: slots[i].i = static_cast<int16_t>(static_cast<int>(v)); break;
      case 8: case 9: slots[i].i = static_cast<int32_t>(v); break;
      case 10: case 11: slots[i].l = static_cast<int64_t>(v); break;
      case 12: slots[i].f = static_cast<float>(v); break;
      case 13: slots[i].d = v; break;
    }
    args[i] = &slots[i];
  }
  const void *rt = g_ui.ret_type(method);
  const int rk = TypeKind(rt);
  if (rk != 1 && !NumericKind(rk))
    return false;
  void *exception = nullptr;
  void *boxed = g_managed.runtime_invoke(method, object, n ? args : nullptr, &exception);
  if (exception)
    return false;
  if (has_result)
    *has_result = rk != 1;
  if (rk == 1 || !boxed)
    return true;
  const void *raw = Unboxed(boxed);
  if (!raw)
    return false;
  double v = 0;
  switch (rk) {
    case 2: case 4: case 5: v = *static_cast<const unsigned char *>(raw); break;
    case 6: v = *static_cast<const int16_t *>(raw); break;
    case 7: v = *static_cast<const uint16_t *>(raw); break;
    case 8: v = *static_cast<const int32_t *>(raw); break;
    case 9: v = *static_cast<const uint32_t *>(raw); break;
    case 10: v = static_cast<double>(*static_cast<const int64_t *>(raw)); break;
    case 11: v = static_cast<double>(*static_cast<const uint64_t *>(raw)); break;
    case 12: v = *static_cast<const float *>(raw); break;
    case 13: v = *static_cast<const double *>(raw); break;
  }
  if (out)
    *out = v;
  return true;
}
// a static field (const fields too) read as a number
bool StaticNumber(void *klass, const char *name, double *out, int *kind) {
  void *field = g_refl.get_field(klass, name);
  if (!field)
    return false;
  const int k = TypeKind(g_refl.field_type(field));
  if (kind)
    *kind = k;
  if (!NumericKind(k))
    return false;
  Slot slot = {};
  g_refl.static_get(field, &slot);
  switch (k) {
    case 2: case 4: case 5: *out = slot.b; break;
    case 6: *out = static_cast<int16_t>(slot.i); break;
    case 7: *out = static_cast<uint16_t>(slot.u); break;
    case 8: *out = slot.i; break;
    case 9: *out = slot.u; break;
    case 10: *out = static_cast<double>(slot.l); break;
    case 11: *out = static_cast<double>(static_cast<uint64_t>(slot.l)); break;
    case 12: *out = slot.f; break;
    case 13: *out = slot.d; break;
  }
  return true;
}
const void *StaticMethod(void *klass, const char *name, int params) {
  for (const void *m : MethodsNamed(klass, name))
    if (!g_refl.is_instance(m) && static_cast<int>(g_refl.param_count(m)) == params)
      return m;
  return nullptr;
}
