// render.hpp -- the figure -> GR translator.
//
// One render is: gr_beginprint(temp file) ... draw ... gr_endprint(), then the
// bytes are read back and the temp file removed.  GR has no in-memory print
// sink (mem:// is a silent no-op), so the temp file is not optional.
//
// Sizing (established empirically against GR 0.73 / cairo on Windows):
//   gr_beginprint() first -- the workstation must exist before the workstation
//   transform can be set, otherwise the call is ignored and you get 2400x2400.
//   Then
//     gr_setwsviewport(0, (W+0.5)*0.0254/600, 0, (H+0.5)*0.0254/600)
//     gr_setwswindow(0, W>=H ? 1 : W/H, 0, H>=W ? 1 : H/W)
//   gives exactly W x H pixels for even W and H.  cairo is hardwired to 600
//   dpi and forces both dimensions even, so odd sizes are rounded down by one
//   before rendering (normalizeSize()).
//
// NOTE the NDC space is NOT the unit square: gr_setwswindow makes the shorter
// axis span less than 1, so every viewport / text coordinate below is scaled
// by ndcW / ndcH.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

#include "colormaps.hpp"
#include "figure.hpp"

extern "C" {
#include "gks.h"
#include "gr.h"
}

namespace grr {

static const int kEncodingUtf8 = 301;  // GR's ENCODING_UTF8

struct Size {
  int w, h;
};

// cairo renders even dimensions only; round odd requests down.
inline Size normalizeSize(int w, int h) {
  if (w < 16) w = 16;
  if (h < 16) h = 16;
  if (w > 8192) w = 8192;
  if (h > 8192) h = 8192;
  return Size{w - (w & 1), h - (h & 1)};
}

struct Extent {
  double lo, hi;
  bool set = false;
  void add(double v) {
    if (!std::isfinite(v)) return;
    if (!set) {
      lo = hi = v;
      set = true;
    } else {
      if (v < lo) lo = v;
      if (v > hi) hi = v;
    }
  }
  void widen() {  // make a degenerate or empty range usable
    if (!set) {
      lo = 0.0;
      hi = 1.0;
      set = true;
      return;
    }
    if (hi - lo <= 0.0) {
      double pad = std::fabs(lo) > 0 ? std::fabs(lo) * 0.05 : 0.5;
      lo -= pad;
      hi += pad;
    }
  }
};

namespace detail {

inline double halfStepLow(const std::vector<double> &a) {
  if (a.size() < 2) return 0.5;
  return (a[1] - a[0]) / 2.0;
}
inline double halfStepHigh(const std::vector<double> &a) {
  if (a.size() < 2) return 0.5;
  return (a[a.size() - 1] - a[a.size() - 2]) / 2.0;
}

// Stride-sample a grid down so contouring cost stays bounded by the output
// size.  gr_cellarray needs no help (it is O(output pixels)); gr_contourf is
// O(grid) and a 2000^2 grid costs ~1.1 s.
inline void decimate(Trace &t, int maxNx, int maxNy) {
  int sx = t.nx > maxNx ? (t.nx + maxNx - 1) / maxNx : 1;
  int sy = t.ny > maxNy ? (t.ny + maxNy - 1) / maxNy : 1;
  if (sx == 1 && sy == 1) return;
  std::vector<double> nxv, nyv, nz;
  for (int j = 0; j < t.nx; j += sx) nxv.push_back(t.x[std::size_t(j)]);
  if (nxv.back() != t.x[std::size_t(t.nx - 1)]) nxv.push_back(t.x[std::size_t(t.nx - 1)]);
  for (int i = 0; i < t.ny; i += sy) nyv.push_back(t.y[std::size_t(i)]);
  if (nyv.back() != t.y[std::size_t(t.ny - 1)]) nyv.push_back(t.y[std::size_t(t.ny - 1)]);
  std::vector<int> ri, ci;
  for (int i = 0; i < t.ny; i += sy) ri.push_back(i);
  if (ri.back() != t.ny - 1) ri.push_back(t.ny - 1);
  for (int j = 0; j < t.nx; j += sx) ci.push_back(j);
  if (ci.back() != t.nx - 1) ci.push_back(t.nx - 1);
  nz.reserve(ri.size() * ci.size());
  for (int i : ri)
    for (int j : ci) nz.push_back(t.z[std::size_t(i) * t.nx + j]);
  t.x.swap(nxv);
  t.y.swap(nyv);
  t.z.swap(nz);
  t.nx = int(ci.size());
  t.ny = int(ri.size());
}

inline std::vector<unsigned char> readAll(const std::string &path) {
  FILE *f = std::fopen(path.c_str(), "rb");
  if (!f) throw std::runtime_error("could not read back the rendered file: " + path);
  std::vector<unsigned char> buf;
  unsigned char chunk[65536];
  std::size_t n;
  while ((n = std::fread(chunk, 1, sizeof chunk, f)) > 0) buf.insert(buf.end(), chunk, chunk + n);
  std::fclose(f);
  if (buf.empty()) throw std::runtime_error("GR produced an empty file: " + path);
  return buf;
}

}  // namespace detail

// Where the plot, its labels and its colorbar live, in NDC.
struct Frame {
  double ndcW = 1.0, ndcH = 1.0;
  double vx0 = 0, vx1 = 0, vy0 = 0, vy1 = 0;
  double cbx0 = 0, cbx1 = 0;  // colorbar strip
  double s = 1.0;             // text scale (the shorter NDC axis)
  bool colorbar = false;
};

class Renderer {
 public:
  Renderer(const Figure &fig, int width, int height) : fig_(fig), w_(width), h_(height) {}

  void draw() {
    prepare();
    resetState();
    layout();
    window();
    drawTraces();
    drawAxes();
    drawLabels();
    if (frame_.colorbar) drawColorbar();
  }

 private:
  Figure fig_;
  int w_, h_;
  Frame frame_;
  Extent xr_, yr_, zr_;
  int gridTrace_ = -1;  // index of the trace that owns the colorbar

  void prepare() {
    for (std::size_t i = 0; i < fig_.traces.size(); ++i) {
      Trace &t = fig_.traces[i];
      if (t.isGrid()) {
        if (gridTrace_ < 0) gridTrace_ = int(i);
        if (t.kind == Kind::Heatmap) {
          // plotly treats heatmap x/y as cell CENTRES, so the axes run half a
          // cell past the outermost coordinates.
          xr_.add(t.x.front() - detail::halfStepLow(t.x));
          xr_.add(t.x.back() + detail::halfStepHigh(t.x));
          yr_.add(t.y.front() - detail::halfStepLow(t.y));
          yr_.add(t.y.back() + detail::halfStepHigh(t.y));
        } else {
          detail::decimate(t, 2 * w_, 2 * h_);  // contouring only; see decimate()
          xr_.add(t.x.front());
          xr_.add(t.x.back());
          yr_.add(t.y.front());
          yr_.add(t.y.back());
        }
        for (double v : t.z) zr_.add(v);
      } else {
        for (double v : t.x) xr_.add(v);
        for (double v : t.y) yr_.add(v);
      }
    }
    xr_.widen();
    yr_.widen();
    zr_.widen();
    // A trace-declared fixed color range (figure.hpp's zfixed) overrides the
    // pooled data range wholesale: gr_setspace, both drawing paths and the
    // colorbar all read zr_, so this one assignment is the entire feature.
    // First declaring grid trace wins, mirroring which trace owns the
    // colorbar.
    for (const Trace &t : fig_.traces)
      if (t.isGrid() && t.zfixed) {
        zr_.lo = t.zmin;
        zr_.hi = t.zmax;
        break;
      }
    // Grid traces must fill their axes exactly (a heatmap with a white margin
    // looks broken), but a line/scatter-only figure gets plotly-ish breathing
    // room: snap the window out to the next tick boundary.
    if (gridTrace_ < 0) {
      gr_adjustlimits(&xr_.lo, &xr_.hi);
      gr_adjustlimits(&yr_.lo, &yr_.hi);
    }
    frame_.colorbar = gridTrace_ >= 0;
  }

  // Every render sets every piece of GR state it depends on, so a render in a
  // long-lived worker is byte-identical to the same render in a fresh process.
  void resetState() {
    gr_setscale(0);
    gr_settextencoding(kEncodingUtf8);
    gr_settextfontprec(232, 3);
    gr_setcharup(0.0, 1.0);
    gr_settextpath(0);
    gr_settextalign(GKS_K_TEXT_HALIGN_NORMAL, GKS_K_TEXT_VALIGN_NORMAL);
    gr_setcharexpan(1.0);
    gr_setcharspace(0.0);
    gr_settextcolorind(1);
    gr_setlinetype(1);
    gr_setlinewidth(1.0);
    gr_setlinecolorind(1);
    gr_setmarkertype(GKS_K_MARKERTYPE_SOLID_CIRCLE);
    gr_setmarkersize(1.0);
    gr_setmarkercolorind(1);
    gr_setfillintstyle(GKS_K_INTSTYLE_SOLID);
    gr_setfillcolorind(1);
    gr_setclip(1);
    installTracePalette();
  }

  void layout() {
    frame_.ndcW = w_ >= h_ ? 1.0 : double(w_) / double(h_);
    frame_.ndcH = h_ >= w_ ? 1.0 : double(h_) / double(w_);
    frame_.s = std::min(frame_.ndcW, frame_.ndcH);
    const double top = fig_.title.empty() ? 0.925 : 0.885;
    frame_.vx0 = (fig_.ylabel.empty() ? 0.125 : 0.165) * frame_.ndcW;
    frame_.vy0 = (fig_.xlabel.empty() ? 0.115 : 0.150) * frame_.ndcH;
    frame_.vy1 = top * frame_.ndcH;

    if (frame_.colorbar) {
      // Text is sized off the SHORT axis but the colorbar's labels eat into
      // the long one, so a tall canvas needs proportionally more room than a
      // fixed fraction of the width gives it.  Reserve ~8 characters plus the
      // tick overhang, measured in the same NDC units the text is drawn in.
      const double gap = 0.028 * frame_.ndcW;
      const double barW = 0.032 * frame_.ndcW;
      const double labelRoom = 5.2 * charHeight(0.026) + 0.014 * frame_.s;
      double right = frame_.ndcW - labelRoom - barW - gap;
      const double minRight = frame_.vx0 + 0.25 * frame_.ndcW;
      if (right < minRight) right = minRight;
      frame_.vx1 = right;
      frame_.cbx0 = right + gap;
      frame_.cbx1 = right + gap + barW;
    } else {
      frame_.vx1 = 0.955 * frame_.ndcW;
    }
  }

  void window() {
    gr_setviewport(frame_.vx0, frame_.vx1, frame_.vy0, frame_.vy1);
    gr_setwindow(xr_.lo, xr_.hi, yr_.lo, yr_.hi);
    gr_setspace(zr_.lo, zr_.hi, 0, 90);
  }

  double charHeight(double factor) const { return factor * frame_.s; }

  int colorIndex(double v) const {
    if (!std::isfinite(v)) return 0;  // background: NaN cells drop out
    double t = (v - zr_.lo) / (zr_.hi - zr_.lo);
    if (!(t >= 0.0)) t = 0.0;
    if (t > 1.0) t = 1.0;
    int idx = 1000 + int(t * 255.0 + 0.5);
    if (idx < 1000) idx = 1000;
    if (idx > 1255) idx = 1255;
    return idx;
  }

  void drawTraces() {
    gr_setclip(1);
    int paletteSlot = 0;
    for (const Trace &t : fig_.traces) {
      switch (t.kind) {
        case Kind::Heatmap:
          drawHeatmap(t);
          break;
        case Kind::ContourFill:
          drawContour(t, true);
          break;
        case Kind::ContourLines:
          drawContour(t, false);
          break;
        case Kind::Scatter:
          drawScatter(t, paletteSlot++);
          break;
      }
    }
  }

  void drawHeatmap(const Trace &t) {
    setColormap(t.colorscale);
    std::vector<int> colors(std::size_t(t.nx) * std::size_t(t.ny));
    for (int i = 0; i < t.ny; ++i)
      for (int j = 0; j < t.nx; ++j)
        colors[std::size_t(i) * t.nx + j] = colorIndex(t.z[std::size_t(i) * t.nx + j]);
    double x0 = t.x.front() - detail::halfStepLow(t.x);
    double x1 = t.x.back() + detail::halfStepHigh(t.x);
    double y0 = t.y.front() - detail::halfStepLow(t.y);
    double y1 = t.y.back() + detail::halfStepHigh(t.y);
    // gr_cellarray fills its colour array from the TOP row down, so the y
    // bounds are handed over swapped to keep row 0 (the smallest y) at the
    // bottom, matching plotly.
    gr_cellarray(x0, x1, y1, y0, t.nx, t.ny, 1, 1, t.nx, t.ny, colors.data());
  }

  void drawContour(const Trace &t, bool filled) {
    setColormap(t.colorscale);
    int nh = t.ncontours;
    if (nh < 2) nh = 2;
    std::vector<double> h(static_cast<std::size_t>(nh));
    for (int i = 0; i < nh; ++i)
      h[std::size_t(i)] = zr_.lo + (zr_.hi - zr_.lo) * double(i) / double(nh - 1);
    // GR's contouring cannot see holes: non-finite cells sink to the floor of
    // the colour range (documented fidelity gap vs plotly's transparent gaps).
    // Finite cells clamp into [zr_.lo, zr_.hi]: a no-op for an automatic
    // range (lo/hi ARE the data extremes) and the defined out-of-range
    // behavior for a fixed one, matching colorIndex's clamp on the heatmap
    // path.
    std::vector<double> z(t.z);
    for (double &v : z) {
      if (!std::isfinite(v)) v = zr_.lo;
      else if (v < zr_.lo) v = zr_.lo;
      else if (v > zr_.hi) v = zr_.hi;
    }
    std::vector<double> x(t.x), y(t.y);
    if (filled) {
      // major_h: 0 draws an UNLABELED black line at every level on top of the
      // fills -- on steep small-scale data (radar cells) the packed lines
      // merge into solid black blobs. A negative value suppresses the lines
      // entirely (measured against GR 0.73.26), which is also much closer to
      // how plotly's filled contours read.
      gr_contourf(t.nx, t.ny, nh, x.data(), y.data(), h.data(), z.data(), -1);
    } else {
      gr_setlinewidth(1.5);
      gr_contour(t.nx, t.ny, nh, x.data(), y.data(), h.data(), z.data(), 1000);
      gr_setlinewidth(1.0);
    }
  }

  void drawScatter(const Trace &t, int slot) {
    const int color = traceColor(slot);
    if (t.lines) {
      gr_setlinecolorind(color);
      gr_setlinewidth(2.0);
      std::vector<double> sx, sy;
      for (std::size_t i = 0; i < t.x.size(); ++i) {
        bool ok = std::isfinite(t.x[i]) && std::isfinite(t.y[i]);
        if (ok) {
          sx.push_back(t.x[i]);
          sy.push_back(t.y[i]);
        } else {
          flushPolyline(sx, sy);
        }
      }
      flushPolyline(sx, sy);
      gr_setlinewidth(1.0);
      gr_setlinecolorind(1);
    }
    if (t.markers) {
      std::vector<double> sx, sy;
      for (std::size_t i = 0; i < t.x.size(); ++i)
        if (std::isfinite(t.x[i]) && std::isfinite(t.y[i])) {
          sx.push_back(t.x[i]);
          sy.push_back(t.y[i]);
        }
      if (!sx.empty()) {
        gr_setmarkertype(GKS_K_MARKERTYPE_SOLID_CIRCLE);
        gr_setmarkersize(1.0);
        gr_setmarkercolorind(color);
        gr_polymarker(int(sx.size()), sx.data(), sy.data());
        gr_setmarkercolorind(1);
      }
    }
  }

  static void flushPolyline(std::vector<double> &sx, std::vector<double> &sy) {
    if (sx.size() >= 2) gr_polyline(int(sx.size()), sx.data(), sy.data());
    sx.clear();
    sy.clear();
  }

  void drawAxes() {
    gr_setclip(0);
    gr_setlinecolorind(1);
    gr_setlinewidth(1.0);
    gr_settextcolorind(1);
    gr_setcharheight(charHeight(0.028));
    const int majorx = 5, majory = 5;
    double xt = gr_tick(xr_.lo, xr_.hi) / majorx;
    double yt = gr_tick(yr_.lo, yr_.hi) / majory;
    const double ticksize = 0.0085 * frame_.s;
    // Left + bottom axes carry the labels; the mirrored pair closes the box.
    gr_axes(xt, yt, xr_.lo, yr_.lo, majorx, majory, ticksize);
    gr_axes(xt, yt, xr_.hi, yr_.hi, -majorx, -majory, -ticksize);
  }

  void text(double x, double y, int halign, int valign, double height,
            const std::string &s) {
    if (s.empty()) return;
    gr_setcharheight(height);
    gr_settextalign(halign, valign);
    gr_text(x, y, const_cast<char *>(s.c_str()));
  }

  void drawLabels() {
    gr_setclip(0);
    gr_settextcolorind(1);
    const double cx = (frame_.vx0 + frame_.vx1) / 2.0;
    const double cy = (frame_.vy0 + frame_.vy1) / 2.0;
    text(cx, frame_.vy1 + 0.030 * frame_.ndcH, GKS_K_TEXT_HALIGN_CENTER,
         GKS_K_TEXT_VALIGN_BOTTOM, charHeight(0.040), fig_.title);
    text(cx, frame_.vy0 - 0.095 * frame_.ndcH, GKS_K_TEXT_HALIGN_CENTER,
         GKS_K_TEXT_VALIGN_BOTTOM, charHeight(0.032), fig_.xlabel);
    if (!fig_.ylabel.empty()) {
      gr_setcharup(-1.0, 0.0);
      text(frame_.vx0 - 0.105 * frame_.ndcW, cy, GKS_K_TEXT_HALIGN_CENTER,
           GKS_K_TEXT_VALIGN_TOP, charHeight(0.032), fig_.ylabel);
      gr_setcharup(0.0, 1.0);
    }
    gr_settextalign(GKS_K_TEXT_HALIGN_NORMAL, GKS_K_TEXT_VALIGN_NORMAL);
  }

  // A colorbar is a viewport-sized paint job, so it MUST be bracketed by
  // savestate/restorestate with a narrow strip viewport of its own -- drawn
  // into the plot's viewport it repaints over the whole plot.
  //
  // gr_colorbar() does exactly this internally, but it phases its tick labels
  // on zmin, which prints seven-significant-digit labels for any real data
  // range (8.54322, 6.04322, ...) and overruns the canvas.  The strip is
  // therefore drawn by hand -- same primitives, tick phase snapped to a round
  // multiple -- which is the only deviation here from the "call gr_colorbar"
  // recipe.
  void drawColorbar() {
    if (gridTrace_ < 0) return;
    gr_savestate();
    setColormap(fig_.traces[std::size_t(gridTrace_)].colorscale);
    gr_setviewport(frame_.cbx0, frame_.cbx1, frame_.vy0, frame_.vy1);
    gr_setwindow(0.0, 1.0, zr_.lo, zr_.hi);
    gr_setspace(zr_.lo, zr_.hi, 0, 90);
    gr_setclip(0);

    const int kBands = 256;
    std::vector<int> bar(kBands);
    for (int i = 0; i < kBands; ++i) bar[std::size_t(i)] = 1000 + i;
    // Same top-row-first convention as the heatmap: hand y over swapped so the
    // low end of the scale sits at the bottom.
    gr_cellarray(0.0, 1.0, zr_.hi, zr_.lo, 1, kBands, 1, 1, 1, kBands, bar.data());

    gr_setlinecolorind(1);
    gr_setlinewidth(1.0);
    gr_setfillintstyle(GKS_K_INTSTYLE_HOLLOW);
    gr_drawrect(0.0, 1.0, zr_.lo, zr_.hi);
    gr_settextcolorind(1);
    gr_setcharheight(charHeight(0.026));
    double zt = 0.5 * gr_tick(zr_.lo, zr_.hi);  // GR.jl's colorbar density
    double zorg = std::ceil(zr_.lo / zt) * zt;  // round tick phase, short labels
    gr_axes(0.0, zt, 1.0, zorg, 0, 1, 0.006 * frame_.s);
    gr_restorestate();
  }
};

// Render `fig` at `w` x `h` into `path` (extension picks the GR driver).
inline void renderToFile(const Figure &fig, int w, int h, const std::string &path) {
  gr_beginprint(const_cast<char *>(path.c_str()));
  // The workstation transform can only be set once the workstation exists,
  // i.e. AFTER gr_beginprint.  Setting it before is silently ignored.
  gr_setwsviewport(0.0, (w + 0.5) * 0.0254 / 600.0, 0.0, (h + 0.5) * 0.0254 / 600.0);
  gr_setwswindow(0.0, w >= h ? 1.0 : double(w) / h, 0.0, h >= w ? 1.0 : double(h) / w);
  try {
    Renderer(fig, w, h).draw();
  } catch (...) {
    gr_endprint();
    throw;
  }
  gr_endprint();
}

}  // namespace grr
