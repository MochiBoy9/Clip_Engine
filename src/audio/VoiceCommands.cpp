#include "audio/VoiceCommands.h"

#include "audio/AudioRouting.h"
#include "core/Clock.h"
#include "core/Log.h"

#include <sapi.h>

#include <algorithm>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <mutex>

namespace clip {

// The mic's audio as a stream speech recognition reads from: 16 kHz mono 16-bit PCM. Read waits
// until there's enough, and ends the stream once closed.
class SpeechStream : public winrt::implements<SpeechStream, IStream> {
 public:
  void Write16(const int16_t* samples, size_t count) {
    {
      std::lock_guard lock(mutex_);
      const auto* bytes = reinterpret_cast<const uint8_t*>(samples);
      bytes_.insert(bytes_.end(), bytes, bytes + count * sizeof(int16_t));
      // If recognition stalls, keep only the last ten seconds rather than growing forever.
      constexpr size_t kMaxBytes = 16000 * 2 * 10;
      if (bytes_.size() > kMaxBytes) bytes_.erase(bytes_.begin(), bytes_.begin() + static_cast<ptrdiff_t>(bytes_.size() - kMaxBytes));
    }
    ready_.notify_one();
  }

  void Close() {
    {
      std::lock_guard lock(mutex_);
      closed_ = true;
    }
    ready_.notify_all();
  }

  // ISequentialStream
  HRESULT STDMETHODCALLTYPE Read(void* buffer, ULONG size, ULONG* read) noexcept override {
    std::unique_lock lock(mutex_);
    ready_.wait(lock, [&] { return closed_ || bytes_.size() >= size; });
    const ULONG n = static_cast<ULONG>(std::min<size_t>(size, bytes_.size()));
    std::copy_n(bytes_.begin(), n, static_cast<uint8_t*>(buffer));
    bytes_.erase(bytes_.begin(), bytes_.begin() + n);
    position_ += n;
    if (read) *read = n;
    return n == size ? S_OK : S_FALSE;
  }
  HRESULT STDMETHODCALLTYPE Write(const void*, ULONG, ULONG*) noexcept override { return STG_E_ACCESSDENIED; }

  // IStream: speech recognition only asks where it is.
  HRESULT STDMETHODCALLTYPE Seek(LARGE_INTEGER move, DWORD origin, ULARGE_INTEGER* position) noexcept override {
    std::lock_guard lock(mutex_);
    const bool here = (origin == STREAM_SEEK_CUR && move.QuadPart == 0) ||
                      (origin == STREAM_SEEK_SET && static_cast<uint64_t>(move.QuadPart) == position_);
    if (!here) return STG_E_INVALIDFUNCTION;
    if (position) position->QuadPart = position_;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE Stat(STATSTG* stat, DWORD) noexcept override {
    if (!stat) return E_POINTER;
    *stat = {};
    stat->type = STGTY_STREAM;
    stat->cbSize.QuadPart = ~0ull >> 1;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE SetSize(ULARGE_INTEGER) noexcept override { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE CopyTo(IStream*, ULARGE_INTEGER, ULARGE_INTEGER*, ULARGE_INTEGER*) noexcept override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE Commit(DWORD) noexcept override { return S_OK; }
  HRESULT STDMETHODCALLTYPE Revert() noexcept override { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE LockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) noexcept override { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE UnlockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) noexcept override { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE Clone(IStream**) noexcept override { return E_NOTIMPL; }

 private:
  std::mutex mutex_;
  std::condition_variable ready_;
  std::deque<uint8_t> bytes_;
  uint64_t position_ = 0;
  bool closed_ = false;
};

namespace {

constexpr ULONG kCommandRule = 1;
constexpr ULONG kOtherSpeechRule = 2;
// After a command, the same words a moment later are an echo, not another clip.
constexpr int64_t kCooldown = 3 * kHnsPerSecond;

// Frees what an event carries, as sphelper.h's SpClearEvent does.
void ClearEvent(SPEVENT& event) {
  switch (event.elParamType) {
    case SPET_LPARAM_IS_OBJECT:
    case SPET_LPARAM_IS_TOKEN:
      if (event.lParam) reinterpret_cast<IUnknown*>(event.lParam)->Release();
      break;
    case SPET_LPARAM_IS_POINTER:
    case SPET_LPARAM_IS_STRING:
      if (event.lParam) CoTaskMemFree(reinterpret_cast<void*>(event.lParam));
      break;
    default:
      break;
  }
  event = {};
}

bool ConfidentEnough(const SPPHRASE& phrase, VoiceSensitivity sensitivity) {
  switch (sensitivity) {
    case VoiceSensitivity::Low: return phrase.Rule.Confidence == SP_HIGH_CONFIDENCE;
    case VoiceSensitivity::Medium: return phrase.Rule.Confidence != SP_LOW_CONFIDENCE;
    case VoiceSensitivity::High: return true;
  }
  return false;
}

winrt::com_ptr<ISpObjectToken> DefaultRecognizer() {
  auto category = winrt::create_instance<ISpObjectTokenCategory>(CLSID_SpObjectTokenCategory);
  winrt::check_hresult(category->SetId(SPCAT_RECOGNIZERS, FALSE));
  LPWSTR id = nullptr;
  winrt::check_hresult(category->GetDefaultTokenId(&id));
  auto token = winrt::create_instance<ISpObjectToken>(CLSID_SpObjectToken);
  const HRESULT hr = token->SetId(nullptr, id, FALSE);
  CoTaskMemFree(id);
  winrt::check_hresult(hr);
  return token;
}

}  // namespace

VoiceCommands::VoiceCommands(std::vector<VoicePhrase> phrases, VoiceSensitivity sensitivity, Heard heard)
    : phrases_(std::move(phrases)), sensitivity_(sensitivity), heard_(std::move(heard)) {
  stream_ = winrt::make_self<SpeechStream>();
  thread_ = std::thread([this] { Run(); });
}

VoiceCommands::~VoiceCommands() {
  stop_ = true;
  stream_->Close();
  if (thread_.joinable()) thread_.join();
}

void VoiceCommands::Hear(const int16_t* pcm, uint32_t frames) {
  if (stop_) return;
  DownmixForSpeech(pcm, frames, carry_, mono_);
  if (!mono_.empty()) stream_->Write16(mono_.data(), mono_.size());
}

void VoiceCommands::Run() {
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  // Behind the game: a command heard a few milliseconds later makes no difference.
  SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
  try {
    auto recognizer = winrt::create_instance<ISpRecognizer>(CLSID_SpInprocRecognizer);
    try {
      winrt::check_hresult(recognizer->SetRecognizer(DefaultRecognizer().get()));
    } catch (const winrt::hresult_error& e) {
      log::Warn("Voice clips need Windows speech recognition, which isn't set up on this PC ({}). Add a speech "
                "language in Windows Settings > Time & language > Language & region.",
                Describe(e));
      winrt::uninit_apartment();
      return;
    }
    // Words aren't learned from the game's chatter, so nothing drifts over a long session.
    recognizer->SetPropertyNum(L"AdaptationOn", 0);

    WAVEFORMATEX format{};
    format.wFormatTag = WAVE_FORMAT_PCM;
    format.nChannels = 1;
    format.nSamplesPerSec = 16000;
    format.wBitsPerSample = 16;
    format.nBlockAlign = 2;
    format.nAvgBytesPerSec = 32000;
    auto input = winrt::create_instance<ISpStream>(CLSID_SpStream);
    winrt::check_hresult(input->SetBaseStream(stream_.get(), SPDFID_WaveFormatEx, &format));
    winrt::check_hresult(recognizer->SetInput(input.get(), TRUE));

    winrt::com_ptr<ISpRecoContext> context;
    winrt::check_hresult(recognizer->CreateRecoContext(context.put()));
    winrt::check_hresult(context->SetNotifyWin32Event());
    winrt::check_hresult(context->SetInterest(SPFEI(SPEI_RECOGNITION), SPFEI(SPEI_RECOGNITION)));

    // One rule for the commands, which may follow other words ("nice, clip that"), and one that
    // takes any other speech, so talking to the squad isn't forced to sound like a command.
    winrt::com_ptr<ISpRecoGrammar> grammar;
    winrt::check_hresult(context->CreateGrammar(1, grammar.put()));
    SPSTATEHANDLE command = nullptr;
    winrt::check_hresult(grammar->GetRule(L"command", kCommandRule, SPRAF_TopLevel | SPRAF_Active, TRUE, &command));
    SPSTATEHANDLE afterOtherWords = nullptr;
    const bool wildcards = SUCCEEDED(grammar->CreateNewState(command, &afterOtherWords)) &&
                           SUCCEEDED(grammar->AddRuleTransition(command, afterOtherWords, SPRULETRANS_WILDCARD, 1.0f, nullptr));
    for (size_t i = 0; i < phrases_.size(); ++i) {
      SPPROPERTYINFO property{};
      property.pszName = L"phrase";
      property.ulId = static_cast<ULONG>(i + 1);
      property.vValue.vt = VT_UI4;
      property.vValue.ulVal = static_cast<ULONG>(i + 1);
      const std::wstring words(winrt::to_hstring(phrases_[i].words));
      winrt::check_hresult(grammar->AddWordTransition(command, nullptr, words.c_str(), L" ", SPWT_LEXICAL, 1.0f, &property));
      if (wildcards) {
        winrt::check_hresult(
            grammar->AddWordTransition(afterOtherWords, nullptr, words.c_str(), L" ", SPWT_LEXICAL, 1.0f, &property));
      }
    }
    if (wildcards) {
      SPSTATEHANDLE other = nullptr;
      if (SUCCEEDED(grammar->GetRule(L"other", kOtherSpeechRule, SPRAF_TopLevel | SPRAF_Active, TRUE, &other))) {
        grammar->AddRuleTransition(other, nullptr, SPRULETRANS_WILDCARD, 1.0f, nullptr);
      }
    }
    winrt::check_hresult(grammar->Commit(0));
    winrt::check_hresult(grammar->SetRuleState(nullptr, nullptr, SPRS_ACTIVE));
    winrt::check_hresult(recognizer->SetRecoState(SPRST_ACTIVE));

    std::string list;
    for (const auto& phrase : phrases_) list += (list.empty() ? "\"" : ", \"") + phrase.words + "\"";
    log::Info("Listening for {} to save a clip", list);

    const HANDLE ready = context->GetNotifyEventHandle();
    int64_t lastHeard = 0;
    while (!stop_) {
      if (WaitForSingleObject(ready, 250) != WAIT_OBJECT_0) continue;
      SPEVENT event{};
      ULONG fetched = 0;
      while (SUCCEEDED(context->GetEvents(1, &event, &fetched)) && fetched == 1) {
        if (event.eEventId == SPEI_RECOGNITION && event.elParamType == SPET_LPARAM_IS_OBJECT && event.lParam) {
          auto* result = reinterpret_cast<ISpRecoResult*>(event.lParam);
          SPPHRASE* phrase = nullptr;
          if (SUCCEEDED(result->GetPhrase(&phrase)) && phrase) {
            const ULONG index = phrase->pProperties ? phrase->pProperties->vValue.ulVal : 0;
            if (phrase->Rule.ulId == kCommandRule && index >= 1 && index <= phrases_.size()) {
              const auto& said = phrases_[index - 1];
              const int64_t now = NowHns();
              if (!ConfidentEnough(*phrase, sensitivity_)) {
                log::Info("Maybe heard \"{}\", not clearly enough to save a clip (confidence {:.2f})", said.words,
                          phrase->Rule.SREngineConfidence);
              } else if (now - lastHeard >= kCooldown) {
                lastHeard = now;
                log::Info("Heard \"{}\" (confidence {:.2f})", said.words, phrase->Rule.SREngineConfidence);
                heard_(said);
              }
            }
            CoTaskMemFree(phrase);
          }
        }
        ClearEvent(event);
      }
    }
    recognizer->SetRecoState(SPRST_INACTIVE_WITH_PURGE);
  } catch (const winrt::hresult_error& e) {
    log::Error("Voice clips stopped: {}", Describe(e));
  }
  winrt::uninit_apartment();
}

}  // namespace clip
