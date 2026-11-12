#ifndef SPARXIE_ENDFIELD_SHARED_H
#define SPARXIE_ENDFIELD_SHARED_H

// The state EndfieldCore (in SessionHost) shares with EndfieldRuntime (injected into the game), one named mapping per game process.
// Both sides include this header, so the layout cannot drift.

#include <stdint.h>

namespace endfield_shared {

// 'SEF2': the second layout (log path added). Core and Runtime ship together in one package.
constexpr uint32_t kMapMagic = 0x53454632u;
constexpr wchar_t kMapPrefix[] = L"Local\\Sparxie.Endfield.";
constexpr int kLogPathChars = 260;

struct SharedState {
  uint32_t magic;
  long sequence;          // bumped by the core when the target frame rate changes
  long alive;             // 0 = the session was released
  long unlock_enabled;
  long touch_ui_enabled;  // 1 = keep the game's input type at Touch
  long target_fps;
  wchar_t log_path[kLogPathChars];  // where the Runtime writes endfield-runtime.log (empty: %TEMP%)
};

}  // namespace endfield_shared

#endif
