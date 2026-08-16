// figure.hpp -- the plotly-figure subset gr-render understands, and the
// reader that turns parsed JSON into it.
//
// The accepted shape is exactly what Blade's stdlib/plot.blade emits:
//
//   {"data":[ trace... ], "layout":{...}}
//
//   trace = {"type":"contour","x":[..],"y":[..],"z":[[..],..],
//            "colorscale":"Viridis","contours":{"coloring":"fill"|"lines"},
//            "ncontours":N}
//         | {"type":"heatmap","x":[..],"y":[..],"z":[[..],..],"colorscale":".."}
//         | {"type":"scatter","mode":"lines"|"markers","x":[..],"y":[..]}
//
//   layout = {"title":{"text":".."},"xaxis":{"title":{"text":".."}},
//             "yaxis":{"title":{"text":".."}},"autosize":true}
//
// z is row-major: z[i][j] is the value at y[i], x[j].  Missing x/y default to
// integer indices.  null / NaN / Infinity all read as non-finite doubles.
#pragma once

#include <algorithm>
#include <cmath>
#include <string>
#include <utility>
#include <vector>

#include "json.hpp"

namespace grr {

enum class Kind { ContourFill, ContourLines, Heatmap, Scatter };

struct Trace {
  Kind kind = Kind::Scatter;
  std::vector<double> x, y;  // grid axes, or scatter point coordinates
  std::vector<double> z;     // row-major ny*nx (grid traces only)
  int nx = 0, ny = 0;
  std::string colorscale = "Viridis";
  int ncontours = 10;
  bool lines = false, markers = false;

  bool isGrid() const { return kind != Kind::Scatter; }
};

struct Figure {
  std::vector<Trace> traces;
  std::string title, xlabel, ylabel;
};

namespace detail {

inline std::vector<double> numArray(const bj::Value &v, const char *what) {
  if (!v.is(bj::Type::Array)) throw bj::Error(std::string(what) + ": expected an array");
  std::vector<double> out;
  out.reserve(v.items.size());
  for (const bj::Value &e : v.items) out.push_back(bj::asNumber(e, what));
  return out;
}

// "title" may be either a bare string or plotly's {"text": "..."} object.
inline std::string titleText(const bj::Value *owner) {
  if (!owner) return std::string();
  const bj::Value *t = owner->get("title");
  if (!t) return std::string();
  if (t->is(bj::Type::String)) return t->text;
  if (t->is(bj::Type::Object)) {
    const bj::Value *txt = t->get("text");
    if (txt && txt->is(bj::Type::String)) return txt->text;
  }
  return std::string();
}

inline void readGrid(const bj::Value &tr, Trace &t) {
  const bj::Value *zv = tr.get("z");
  if (!zv) throw bj::Error("trace: 'z' is required for contour/heatmap");
  if (!zv->is(bj::Type::Array) || zv->items.empty())
    throw bj::Error("trace.z: expected a non-empty array of rows");
  t.ny = int(zv->items.size());
  for (int i = 0; i < t.ny; ++i) {
    const bj::Value &row = zv->items[std::size_t(i)];
    if (!row.is(bj::Type::Array)) throw bj::Error("trace.z: every row must be an array");
    if (i == 0) {
      t.nx = int(row.items.size());
      if (t.nx == 0) throw bj::Error("trace.z: rows must be non-empty");
      t.z.reserve(std::size_t(t.nx) * std::size_t(t.ny));
    } else if (int(row.items.size()) != t.nx) {
      throw bj::Error("trace.z: rows have differing lengths (" +
                      std::to_string(row.items.size()) + " vs " + std::to_string(t.nx) + ")");
    }
    for (const bj::Value &e : row.items) t.z.push_back(bj::asNumber(e, "trace.z"));
  }

  const bj::Value *xv = tr.get("x");
  const bj::Value *yv = tr.get("y");
  if (xv && !xv->isNull()) {
    t.x = numArray(*xv, "trace.x");
    if (int(t.x.size()) != t.nx)
      throw bj::Error("trace.x has " + std::to_string(t.x.size()) + " entries but z rows are " +
                      std::to_string(t.nx) + " wide");
  } else {
    t.x.resize(std::size_t(t.nx));
    for (int j = 0; j < t.nx; ++j) t.x[std::size_t(j)] = j;
  }
  if (yv && !yv->isNull()) {
    t.y = numArray(*yv, "trace.y");
    if (int(t.y.size()) != t.ny)
      throw bj::Error("trace.y has " + std::to_string(t.y.size()) + " entries but z has " +
                      std::to_string(t.ny) + " rows");
  } else {
    t.y.resize(std::size_t(t.ny));
    for (int i = 0; i < t.ny; ++i) t.y[std::size_t(i)] = i;
  }

  for (double v : t.x)
    if (!std::isfinite(v)) throw bj::Error("trace.x: grid axes must be finite");
  for (double v : t.y)
    if (!std::isfinite(v)) throw bj::Error("trace.y: grid axes must be finite");

  // GR wants strictly ascending grid axes.  A strictly descending axis is
  // flipped in place (with its z data); anything else is an error.
  auto ascending = [](const std::vector<double> &v) {
    for (std::size_t i = 1; i < v.size(); ++i)
      if (v[i] <= v[i - 1]) return false;
    return true;
  };
  auto descending = [](const std::vector<double> &v) {
    for (std::size_t i = 1; i < v.size(); ++i)
      if (v[i] >= v[i - 1]) return false;
    return true;
  };
  if (t.nx > 1 && !ascending(t.x)) {
    if (!descending(t.x)) throw bj::Error("trace.x: grid axis must be monotonic");
    for (int i = 0; i < t.ny; ++i)
      for (int j = 0; j < t.nx / 2; ++j)
        std::swap(t.z[std::size_t(i) * t.nx + j], t.z[std::size_t(i) * t.nx + (t.nx - 1 - j)]);
    for (int j = 0; j < t.nx / 2; ++j) std::swap(t.x[std::size_t(j)], t.x[std::size_t(t.nx - 1 - j)]);
  }
  if (t.ny > 1 && !ascending(t.y)) {
    if (!descending(t.y)) throw bj::Error("trace.y: grid axis must be monotonic");
    for (int i = 0; i < t.ny / 2; ++i)
      for (int j = 0; j < t.nx; ++j)
        std::swap(t.z[std::size_t(i) * t.nx + j],
                  t.z[std::size_t(t.ny - 1 - i) * t.nx + j]);
    for (int i = 0; i < t.ny / 2; ++i) std::swap(t.y[std::size_t(i)], t.y[std::size_t(t.ny - 1 - i)]);
  }
}

}  // namespace detail

inline Figure readFigure(const bj::Value &root) {
  if (!root.is(bj::Type::Object)) throw bj::Error("figure: expected a JSON object");
  const bj::Value *data = root.get("data");
  if (!data || !data->is(bj::Type::Array))
    throw bj::Error("figure.data: expected an array of traces");
  if (data->items.empty()) throw bj::Error("figure.data: no traces to render");

  Figure fig;
  for (const bj::Value &tr : data->items) {
    if (!tr.is(bj::Type::Object)) throw bj::Error("figure.data: every trace must be an object");
    const bj::Value *ty = tr.get("type");
    std::string type = ty && ty->is(bj::Type::String) ? ty->text : std::string("scatter");

    Trace t;
    const bj::Value *cs = tr.get("colorscale");
    if (cs && cs->is(bj::Type::String)) t.colorscale = cs->text;

    if (type == "contour") {
      std::string coloring = "fill";
      const bj::Value *c = tr.get("contours");
      if (c && c->is(bj::Type::Object)) {
        const bj::Value *col = c->get("coloring");
        if (col && col->is(bj::Type::String)) coloring = col->text;
      }
      t.kind = (coloring == "lines" || coloring == "none") ? Kind::ContourLines : Kind::ContourFill;
      const bj::Value *nc = tr.get("ncontours");
      if (nc && nc->is(bj::Type::Number)) {
        double n = nc->number;
        if (!(n >= 2)) n = 2;
        if (n > 256) n = 256;
        t.ncontours = int(n);
      }
      detail::readGrid(tr, t);
    } else if (type == "heatmap") {
      t.kind = Kind::Heatmap;
      detail::readGrid(tr, t);
    } else if (type == "scatter" || type == "scattergl") {
      t.kind = Kind::Scatter;
      std::string mode = "lines";
      const bj::Value *m = tr.get("mode");
      if (m && m->is(bj::Type::String)) mode = m->text;
      t.lines = mode.find("lines") != std::string::npos;
      t.markers = mode.find("markers") != std::string::npos;
      if (!t.lines && !t.markers) t.lines = true;  // "none" etc: draw something
      const bj::Value *xv = tr.get("x");
      const bj::Value *yv = tr.get("y");
      if (!yv) throw bj::Error("scatter trace: 'y' is required");
      t.y = detail::numArray(*yv, "trace.y");
      if (xv && !xv->isNull()) {
        t.x = detail::numArray(*xv, "trace.x");
      } else {
        t.x.resize(t.y.size());
        for (std::size_t i = 0; i < t.y.size(); ++i) t.x[i] = double(i);
      }
      if (t.x.size() != t.y.size())
        throw bj::Error("scatter trace: x and y have different lengths (" +
                        std::to_string(t.x.size()) + " vs " + std::to_string(t.y.size()) + ")");
      if (t.x.empty()) throw bj::Error("scatter trace: x/y are empty");
    } else {
      throw bj::Error("figure.data: unsupported trace type '" + type + "'");
    }
    fig.traces.push_back(std::move(t));
  }

  const bj::Value *layout = root.get("layout");
  if (layout && layout->is(bj::Type::Object)) {
    fig.title = detail::titleText(layout);
    const bj::Value *xa = layout->get("xaxis");
    const bj::Value *ya = layout->get("yaxis");
    if (xa && xa->is(bj::Type::Object)) fig.xlabel = detail::titleText(xa);
    if (ya && ya->is(bj::Type::Object)) fig.ylabel = detail::titleText(ya);
  }
  return fig;
}

}  // namespace grr
