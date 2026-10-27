#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace clip {

// Splits a byte stream of back-to-back JSON objects ({...}{...} or one per line) into whole
// objects, however the bytes happen to be cut into reads.
class JsonObjectStream {
 public:
  void Feed(std::string_view bytes);
  // The next complete object, if one has arrived.
  std::optional<std::string> Next();

 private:
  std::string buffer_;
  size_t scanned_ = 0;
  size_t start_ = std::string::npos;
  int depth_ = 0;
  bool inString_ = false;
  bool escaped_ = false;
};

// Reads WebSocket frames a server sends (RFC 6455: never masked, may be fragmented).
class WebSocketReader {
 public:
  enum class Kind { Text, Ping, Close, Other };
  struct Message {
    Kind kind = Kind::Other;
    std::string payload;
  };

  void Feed(std::string_view bytes) { buffer_.append(bytes); }
  std::optional<Message> Next();

  // A frame from the client side, which must be masked: a pong answering a ping, for example.
  static std::string ClientFrame(int opcode, std::string_view payload, uint32_t mask);

 private:
  std::string buffer_;
  std::string partial_;  // a fragmented text message so far
  bool inText_ = false;
};

}  // namespace clip
