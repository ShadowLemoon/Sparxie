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
