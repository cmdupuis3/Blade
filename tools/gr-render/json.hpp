// json.hpp -- a small, strict, hand-rolled JSON reader for gr-render.
//
// Standard RFC 8259 grammar plus three bare tokens that Blade's stdlib
// currently emits for non-finite numbers: NaN, Infinity, -Infinity.  `null`
// also reads as a non-finite number when a number is expected (that is the
// spelling plotly wants, and the spelling stdlib is moving to), so both the
// buggy and the fixed emitter parse here.
//
// Everything else is rejected with a byte offset.  No third-party code.
#pragma once

#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

namespace bj {

class Error : public std::runtime_error {
 public:
  explicit Error(const std::string &m) : std::runtime_error(m) {}
};

enum class Type { Null, Bool, Number, String, Array, Object };

// A parsed value.  Objects keep insertion order in two parallel vectors so
// that Value stays usable while it is still an incomplete type.
struct Value {
  Type type = Type::Null;
  bool boolean = false;
  double number = 0.0;
  std::string text;
  std::vector<Value> items;       // Array elements
  std::vector<std::string> keys;  // Object keys
  std::vector<Value> vals;        // Object values (parallel to keys)

  bool is(Type t) const { return type == t; }
  bool isNull() const { return type == Type::Null; }

  const Value *get(const char *key) const {
    if (type != Type::Object) return nullptr;
    for (std::size_t i = 0; i < keys.size(); ++i)
      if (keys[i] == key) return &vals[i];
    return nullptr;
  }
};

class Parser {
 public:
  explicit Parser(const std::string &src) : s_(src) {}

  Value parse() {
    if (s_.compare(0, 3, "\xEF\xBB\xBF") == 0) p_ = 3;  // tolerate a UTF-8 BOM
    skipWs();
    Value v = parseValue(0);
    skipWs();
    if (p_ != s_.size()) fail("trailing content after top-level value");
    return v;
  }

 private:
  static const int kMaxDepth = 200;

  const std::string &s_;
  std::size_t p_ = 0;

  [[noreturn]] void fail(const std::string &msg) const {
    throw Error("JSON: " + msg + " (at byte " + std::to_string(p_) + ")");
  }

  bool eof() const { return p_ >= s_.size(); }
  char peek() const {
    if (eof()) fail("unexpected end of input");
    return s_[p_];
  }

  void skipWs() {
    while (p_ < s_.size()) {
      char c = s_[p_];
      if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
        ++p_;
      else
        break;
    }
  }

  // strlen, spelled locally so the header needs no <cstring>.
  struct StrLen {
    static std::size_t of(const char *w) {
      std::size_t n = 0;
      while (w[n]) ++n;
      return n;
    }
  };

  Value parseValue(int depth) {
    if (depth > kMaxDepth) fail("nesting too deep");
    char c = peek();
    switch (c) {
      case '{':
        return parseObject(depth);
      case '[':
        return parseArray(depth);
      case '"': {
        Value v;
        v.type = Type::String;
        v.text = parseString();
        return v;
      }
      case 't': {
        expect("true");
        Value v;
        v.type = Type::Bool;
        v.boolean = true;
        return v;
      }
      case 'f': {
        expect("false");
        Value v;
        v.type = Type::Bool;
        v.boolean = false;
        return v;
      }
      case 'n': {
        expect("null");
        return Value();  // Type::Null
      }
      case 'N': {  // bare NaN (stdlib emits this today)
        expect("NaN");
        return num(std::numeric_limits<double>::quiet_NaN());
      }
      case 'I': {  // bare Infinity
        expect("Infinity");
        return num(std::numeric_limits<double>::infinity());
      }
      default:
        if (c == '-' && s_.compare(p_, 9, "-Infinity") == 0) {
          p_ += 9;
          return num(-std::numeric_limits<double>::infinity());
        }
        if (c == '-' || (c >= '0' && c <= '9')) return parseNumber();
        fail(std::string("unexpected character '") + c + "'");
    }
  }

  static Value num(double d) {
    Value v;
    v.type = Type::Number;
    v.number = d;
    return v;
  }

  void expect(const char *word) {
    std::size_t n = StrLen::of(word);
    if (s_.compare(p_, n, word) != 0)
      fail(std::string("expected '") + word + "'");
    p_ += n;
  }

  Value parseObject(int depth) {
    Value v;
    v.type = Type::Object;
    ++p_;  // '{'
    skipWs();
    if (peek() == '}') {
      ++p_;
      return v;
    }
    for (;;) {
      skipWs();
      if (peek() != '"') fail("expected a string key");
      std::string key = parseString();
      skipWs();
      if (peek() != ':') fail("expected ':' after key");
      ++p_;
      skipWs();
      v.keys.push_back(key);
      v.vals.push_back(parseValue(depth + 1));
      skipWs();
      char c = peek();
      if (c == ',') {
        ++p_;
        continue;
      }
      if (c == '}') {
        ++p_;
        return v;
      }
      fail("expected ',' or '}' in object");
    }
  }

  Value parseArray(int depth) {
    Value v;
    v.type = Type::Array;
    ++p_;  // '['
    skipWs();
    if (peek() == ']') {
      ++p_;
      return v;
    }
    for (;;) {
      skipWs();
      v.items.push_back(parseValue(depth + 1));
      skipWs();
      char c = peek();
      if (c == ',') {
        ++p_;
        continue;
      }
      if (c == ']') {
        ++p_;
        return v;
      }
      fail("expected ',' or ']' in array");
    }
  }

  static void utf8(std::string &out, unsigned cp) {
    if (cp < 0x80) {
      out += static_cast<char>(cp);
    } else if (cp < 0x800) {
      out += static_cast<char>(0xC0 | (cp >> 6));
      out += static_cast<char>(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
      out += static_cast<char>(0xE0 | (cp >> 12));
      out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
      out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
      out += static_cast<char>(0xF0 | (cp >> 18));
      out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
      out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
      out += static_cast<char>(0x80 | (cp & 0x3F));
    }
  }

  unsigned hex4() {
    if (p_ + 4 > s_.size()) fail("truncated \\u escape");
    unsigned v = 0;
    for (int i = 0; i < 4; ++i) {
      char c = s_[p_ + i];
      v <<= 4;
      if (c >= '0' && c <= '9')
        v |= unsigned(c - '0');
      else if (c >= 'a' && c <= 'f')
        v |= unsigned(c - 'a' + 10);
      else if (c >= 'A' && c <= 'F')
        v |= unsigned(c - 'A' + 10);
      else
        fail("bad hex digit in \\u escape");
    }
    p_ += 4;
    return v;
  }

  std::string parseString() {
    ++p_;  // opening quote
    std::string out;
    for (;;) {
      if (eof()) fail("unterminated string");
      unsigned char c = static_cast<unsigned char>(s_[p_]);
      if (c == '"') {
        ++p_;
        return out;
      }
      if (c < 0x20) fail("raw control character in string");
      if (c != '\\') {
        out += static_cast<char>(c);
        ++p_;
        continue;
      }
      ++p_;  // backslash
      if (eof()) fail("unterminated escape");
      char e = s_[p_++];
      switch (e) {
        case '"': out += '"'; break;
        case '\\': out += '\\'; break;
        case '/': out += '/'; break;
        case 'b': out += '\b'; break;
        case 'f': out += '\f'; break;
        case 'n': out += '\n'; break;
        case 'r': out += '\r'; break;
        case 't': out += '\t'; break;
        case 'u': {
          unsigned cp = hex4();
          if (cp >= 0xD800 && cp <= 0xDBFF) {  // high surrogate
            if (p_ + 1 < s_.size() && s_[p_] == '\\' && s_[p_ + 1] == 'u') {
              std::size_t save = p_;
              p_ += 2;
              unsigned lo = hex4();
              if (lo >= 0xDC00 && lo <= 0xDFFF) {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
              } else {
                p_ = save;
                cp = 0xFFFD;
              }
            } else {
              cp = 0xFFFD;
            }
          } else if (cp >= 0xDC00 && cp <= 0xDFFF) {
            cp = 0xFFFD;  // lone low surrogate
          }
          utf8(out, cp);
          break;
        }
        default:
          fail(std::string("unknown escape '\\") + e + "'");
      }
    }
  }

  Value parseNumber() {
    std::size_t start = p_;
    if (!eof() && s_[p_] == '-') ++p_;
    // int part: 0 | [1-9][0-9]*
    if (eof()) fail("truncated number");
    if (s_[p_] == '0') {
      ++p_;
    } else if (s_[p_] >= '1' && s_[p_] <= '9') {
      while (!eof() && s_[p_] >= '0' && s_[p_] <= '9') ++p_;
    } else {
      fail("expected a digit");
    }
    if (!eof() && s_[p_] == '.') {
      ++p_;
      if (eof() || s_[p_] < '0' || s_[p_] > '9') fail("expected a digit after '.'");
      while (!eof() && s_[p_] >= '0' && s_[p_] <= '9') ++p_;
    }
    if (!eof() && (s_[p_] == 'e' || s_[p_] == 'E')) {
      ++p_;
      if (!eof() && (s_[p_] == '+' || s_[p_] == '-')) ++p_;
      if (eof() || s_[p_] < '0' || s_[p_] > '9') fail("expected a digit in exponent");
      while (!eof() && s_[p_] >= '0' && s_[p_] <= '9') ++p_;
    }
    std::string tok = s_.substr(start, p_ - start);
    return num(std::strtod(tok.c_str(), nullptr));
  }
};

inline Value parse(const std::string &src) { return Parser(src).parse(); }

// ---- typed accessors -------------------------------------------------------

// A JSON number, or NaN for `null` / a bare non-finite token.  Anything else
// (string, object, ...) is an error.
inline double asNumber(const Value &v, const char *what) {
  if (v.is(Type::Number)) return v.number;
  if (v.isNull()) return std::numeric_limits<double>::quiet_NaN();
  throw Error(std::string(what) + ": expected a number");
}

inline const std::string &asString(const Value &v, const char *what) {
  if (!v.is(Type::String)) throw Error(std::string(what) + ": expected a string");
  return v.text;
}

// Optional string member, following plotly's {"title":{"text":"..."}} shape
// when `nested` is given.
inline std::string optString(const Value *owner, const char *key) {
  if (!owner) return std::string();
  const Value *m = owner->get(key);
  if (!m || !m->is(Type::String)) return std::string();
  return m->text;
}

inline std::string escape(const std::string &s) {
  std::string out;
  out.reserve(s.size() + 8);
  for (unsigned char c : s) {
    switch (c) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\b': out += "\\b"; break;
      case '\f': out += "\\f"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '\t': out += "\\t"; break;
      default:
        if (c < 0x20) {
          static const char *hexd = "0123456789abcdef";
          out += "\\u00";
          out += hexd[(c >> 4) & 0xF];
          out += hexd[c & 0xF];
        } else {
          out += static_cast<char>(c);
        }
    }
  }
  return out;
}

}  // namespace bj
