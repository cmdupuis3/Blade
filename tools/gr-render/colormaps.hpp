// colormaps.hpp -- the five colorscales stdlib/plot.blade can name.
//
// Viridis and Plasma exist in GR as built-ins (indices 44 / 46) and are used
// as such.  Cividis, Greys and RdBu do not exist in GR, so they are installed
// with gr_setcolormapfromrgb() from plotly's own anchor tables.  Every path
// leaves GR's colormap occupying colour indices 1000..1255.
#pragma once

#include <string>
#include <vector>

extern "C" {
#include "gr.h"
}

namespace grr {

struct Anchor {
  double pos, r, g, b;  // pos in [0,1], channels in [0,1]
};

// plotly.js Cividis.
static const Anchor kCividis[] = {
    {0.0, 0 / 255.0, 32 / 255.0, 76 / 255.0},
    {0.1, 0 / 255.0, 42 / 255.0, 102 / 255.0},
    {0.2, 0 / 255.0, 52 / 255.0, 110 / 255.0},
    {0.3, 39 / 255.0, 63 / 255.0, 108 / 255.0},
    {0.4, 60 / 255.0, 74 / 255.0, 107 / 255.0},
    {0.5, 76 / 255.0, 85 / 255.0, 107 / 255.0},
    {0.6, 91 / 255.0, 99 / 255.0, 105 / 255.0},
    {0.7, 111 / 255.0, 115 / 255.0, 103 / 255.0},
    {0.8, 134 / 255.0, 134 / 255.0, 95 / 255.0},
    {0.9, 159 / 255.0, 155 / 255.0, 80 / 255.0},
    {1.0, 187 / 255.0, 180 / 255.0, 56 / 255.0},
};

// plotly's sequential Greys: light at the low end, dark at the high end.
static const Anchor kGreys[] = {
    {0.0, 1.0, 1.0, 1.0},
    {0.5, 0.5, 0.5, 0.5},
    {1.0, 0.0, 0.0, 0.0},
};

// plotly.js RdBu (diverging, blue low -> grey mid -> red high), with its
// non-uniform anchor positions preserved.
static const Anchor kRdBu[] = {
    {0.00, 5 / 255.0, 10 / 255.0, 172 / 255.0},
    {0.35, 106 / 255.0, 137 / 255.0, 247 / 255.0},
    {0.50, 190 / 255.0, 190 / 255.0, 190 / 255.0},
    {0.60, 220 / 255.0, 170 / 255.0, 132 / 255.0},
    {0.70, 230 / 255.0, 145 / 255.0, 90 / 255.0},
    {1.00, 178 / 255.0, 10 / 255.0, 28 / 255.0},
};

inline void installAnchors(const Anchor *a, int n) {
  std::vector<double> r(n), g(n), b(n), x(n);
  for (int i = 0; i < n; ++i) {
    r[i] = a[i].r;
    g[i] = a[i].g;
    b[i] = a[i].b;
    x[i] = a[i].pos;
  }
  gr_setcolormapfromrgb(n, r.data(), g.data(), b.data(), x.data());
}

inline std::string lower(const std::string &s) {
  std::string o = s;
  for (char &c : o)
    if (c >= 'A' && c <= 'Z') c = char(c - 'A' + 'a');
  return o;
}

// Make `name` the current GR colormap.  Unknown names fall back to Viridis,
// matching plot.blade's own out-of-table behaviour.
inline void setColormap(const std::string &name) {
  const std::string n = lower(name);
  if (n == "plasma") {
    gr_setcolormap(GR_COLORMAP_PLASMA);
  } else if (n == "cividis") {
    installAnchors(kCividis, int(sizeof kCividis / sizeof kCividis[0]));
  } else if (n == "greys" || n == "grays" || n == "greyscale") {
    installAnchors(kGreys, int(sizeof kGreys / sizeof kGreys[0]));
  } else if (n == "rdbu") {
    installAnchors(kRdBu, int(sizeof kRdBu / sizeof kRdBu[0]));
  } else {
    gr_setcolormap(GR_COLORMAP_VIRIDIS);
  }
}

// plotly's default qualitative trace palette (D3 category10), installed at
// colour indices 980..989 -- above GR's predefined table, below the colormap.
static const double kTracePalette[10][3] = {
    {0.121569, 0.466667, 0.705882}, {1.000000, 0.498039, 0.054902},
    {0.172549, 0.627451, 0.172549}, {0.839216, 0.152941, 0.156863},
    {0.580392, 0.403922, 0.741176}, {0.549020, 0.337255, 0.294118},
    {0.890196, 0.466667, 0.760784}, {0.498039, 0.498039, 0.498039},
    {0.737255, 0.741176, 0.133333}, {0.090196, 0.745098, 0.811765},
};
static const int kTraceColorBase = 980;
static const int kTraceColorCount = 10;

inline void installTracePalette() {
  for (int i = 0; i < kTraceColorCount; ++i)
    gr_setcolorrep(kTraceColorBase + i, kTracePalette[i][0], kTracePalette[i][1],
                   kTracePalette[i][2]);
}

inline int traceColor(int index) {
  return kTraceColorBase + (index % kTraceColorCount);
}

}  // namespace grr
