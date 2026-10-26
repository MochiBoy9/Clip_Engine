#pragma once

#include <filesystem>
#include <format>
#include <string_view>

namespace clip::log {

enum class Level { Info, Warn, Error };

// Mirrors every line to the console and to the given file.
void Init(const std::filesystem::path& file);
void Write(Level level, std::string_view message);

template <class... Args>
void Info(std::format_string<Args...> fmt, Args&&... args) {
  Write(Level::Info, std::format(fmt, std::forward<Args>(args)...));
}

template <class... Args>
void Warn(std::format_string<Args...> fmt, Args&&... args) {
  Write(Level::Warn, std::format(fmt, std::forward<Args>(args)...));
}

template <class... Args>
void Error(std::format_string<Args...> fmt, Args&&... args) {
  Write(Level::Error, std::format(fmt, std::forward<Args>(args)...));
}

}  // namespace clip::log
