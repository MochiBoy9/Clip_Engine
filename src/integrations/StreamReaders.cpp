#include "integrations/StreamReaders.h"

namespace clip {

void JsonObjectStream::Feed(std::string_view bytes) { buffer_.append(bytes); }

std::optional<std::string> JsonObjectStream::Next() {
  for (; scanned_ < buffer_.size(); ++scanned_) {
    const char c = buffer_[scanned_];
    if (inString_) {
      if (escaped_) escaped_ = false;
      else if (c == '\\') escaped_ = true;
      else if (c == '"') inString_ = false;
      continue;
    }
    if (depth_ == 0 && c != '{') continue;  // separators and anything between objects
    if (c == '"') {
      inString_ = true;
    } else if (c == '{') {
      if (depth_++ == 0) start_ = scanned_;
    } else if (c == '}' && --depth_ == 0) {
      std::string object = buffer_.substr(start_, scanned_ + 1 - start_);
      buffer_.erase(0, scanned_ + 1);
      scanned_ = 0;
      start_ = std::string::npos;
      return object;
    }
  }
  // Nothing open: drop what's been scanned so the buffer doesn't grow forever.
  if (depth_ == 0) {
    buffer_.clear();
    scanned_ = 0;
  }
  return std::nullopt;
}

std::optional<WebSocketReader::Message> WebSocketReader::Next() {
  while (true) {
    if (buffer_.size() < 2) return std::nullopt;
    const auto byte = [&](size_t i) { return static_cast<uint8_t>(buffer_[i]); };
    const bool fin = (byte(0) & 0x80) != 0;
    const int opcode = byte(0) & 0x0F;
    const bool masked = (byte(1) & 0x80) != 0;
    uint64_t length = byte(1) & 0x7F;
    size_t header = 2;
    if (length == 126) {
      if (buffer_.size() < 4) return std::nullopt;
      length = (uint64_t{byte(2)} << 8) | byte(3);
      header = 4;
    } else if (length == 127) {
      if (buffer_.size() < 10) return std::nullopt;
      length = 0;
      for (size_t i = 2; i < 10; ++i) length = (length << 8) | byte(i);
      header = 10;
    }
    const size_t maskOffset = header;
    if (masked) header += 4;
    if (length > (64u << 20)) {  // nothing legitimate is this big; give up on the stream
      buffer_.clear();
      return Message{Kind::Close, {}};
    }
    if (buffer_.size() < header + length) return std::nullopt;

    std::string payload = buffer_.substr(header, static_cast<size_t>(length));
    if (masked) {
      for (size_t i = 0; i < payload.size(); ++i) payload[i] = static_cast<char>(payload[i] ^ buffer_[maskOffset + i % 4]);
    }
    buffer_.erase(0, header + static_cast<size_t>(length));

    if (opcode == 0x8) return Message{Kind::Close, std::move(payload)};
    if (opcode == 0x9) return Message{Kind::Ping, std::move(payload)};
    if (opcode == 0x1 || (opcode == 0x0 && inText_)) {
      partial_ += payload;
      inText_ = !fin;
      if (fin) {
        Message message{Kind::Text, std::move(partial_)};
        partial_.clear();
        return message;
      }
      continue;
    }
    // Pongs and binary frames carry nothing we use.
    return Message{Kind::Other, std::move(payload)};
  }
}

std::string WebSocketReader::ClientFrame(int opcode, std::string_view payload, uint32_t mask) {
  std::string frame;
  frame += static_cast<char>(0x80 | (opcode & 0x0F));
  if (payload.size() < 126) {
    frame += static_cast<char>(0x80 | payload.size());
  } else {
    // Control frames are always short; this is only for completeness.
    frame += static_cast<char>(0x80 | 126);
    frame += static_cast<char>((payload.size() >> 8) & 0xFF);
    frame += static_cast<char>(payload.size() & 0xFF);
  }
  const char key[4] = {static_cast<char>(mask >> 24), static_cast<char>(mask >> 16), static_cast<char>(mask >> 8),
                       static_cast<char>(mask)};
  frame.append(key, 4);
  for (size_t i = 0; i < payload.size(); ++i) frame += static_cast<char>(payload[i] ^ key[i % 4]);
  return frame;
}

}  // namespace clip
