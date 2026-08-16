// base64.hpp -- standard base64 encoder (RFC 4648, with padding, no wrapping).
#pragma once

#include <string>
#include <vector>

namespace grr {

inline std::string base64(const std::vector<unsigned char> &in) {
  static const char *tbl =
      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  std::string out;
  out.reserve(((in.size() + 2) / 3) * 4);
  std::size_t i = 0;
  for (; i + 3 <= in.size(); i += 3) {
    unsigned v = (unsigned(in[i]) << 16) | (unsigned(in[i + 1]) << 8) | in[i + 2];
    out += tbl[(v >> 18) & 63];
    out += tbl[(v >> 12) & 63];
    out += tbl[(v >> 6) & 63];
    out += tbl[v & 63];
  }
  std::size_t rest = in.size() - i;
  if (rest == 1) {
    unsigned v = unsigned(in[i]) << 16;
    out += tbl[(v >> 18) & 63];
    out += tbl[(v >> 12) & 63];
    out += "==";
  } else if (rest == 2) {
    unsigned v = (unsigned(in[i]) << 16) | (unsigned(in[i + 1]) << 8);
    out += tbl[(v >> 18) & 63];
    out += tbl[(v >> 12) & 63];
    out += tbl[(v >> 6) & 63];
    out += '=';
  }
  return out;
}

}  // namespace grr
