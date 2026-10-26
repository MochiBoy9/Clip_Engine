#include "core/Log.h"

#include "core/Win.h"

#include <cstdio>
#include <fstream>
#include <mutex>

namespace clip::log {
namespace {

std::mutex g_mutex;
std::ofstream g_file;

const char* LevelName(Level level) {
  switch (level) {
    case Level::Info: return "INFO ";
    case Level::Warn: return "WARN ";
    default: return "ERROR";
  }
}

}  // namespace

void Init(const std::filesystem::path& file) {
  std::lock_guard lock(g_mutex);
  std::error_code ec;
  std::filesystem::create_directories(file.parent_path(), ec);
  g_file.open(file, std::ios::app);
}

void Write(Level level, std::string_view message) {
  SYSTEMTIME t;
  GetLocalTime(&t);
  const std::string line = std::format("{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03} {} {}\n", t.wYear,
                                       t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond,
                                       t.wMilliseconds, LevelName(level), message);
  std::lock_guard lock(g_mutex);
  std::fputs(line.c_str(), level == Level::Info ? stdout : stderr);
  if (g_file) {
    g_file << line;
    g_file.flush();
  }
}

}  // namespace clip::log
