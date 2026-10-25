#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <propvarutil.h>
#include <robuffer.h>
#include <roapi.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Media.Core.h>
#include <winrt/Windows.Media.MediaProperties.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.System.Threading.h>
#include <atomic>
#include <algorithm>
#include <chrono>
#include <memory>
#include <mutex>
#include <vector>
#include "AudioTempo.h"

using namespace winrt;
using namespace winrt::Windows::Media::Core;
using namespace winrt::Windows::Media::MediaProperties;
using winrt::Windows::Foundation::TimeSpan;

namespace {
using Clock = std::chrono::steady_clock;
double Milliseconds(Clock::time_point start) { return std::chrono::duration<double, std::milli>(Clock::now() - start).count(); }

// Delivery diagnostics, not speaker/display presentation timestamps.
struct SourceStatistics {
    uint64_t epoch = 0, audioSamples = 0, videoSamples = 0, audioGaps = 0, streamTicks = 0;
    int64_t audioEnd = -1, videoTimestamp = -1, maxAudioGap = 0, duration = 0;
    double audioRequestMaxMs = 0, videoRequestMaxMs = 0, readMaxMs = 0;
};
struct Apartment {
    Apartment() { check_hresult(RoInitialize(RO_INIT_MULTITHREADED)); }
    ~Apartment() { RoUninitialize(); }
};
struct Track {
    DWORD index;
    IMediaStreamDescriptor descriptor;
    bool video;
    uint32_t channels = 0;
    std::unique_ptr<AudioTempo> tempo;
};
struct State {
    com_ptr<IMFSourceReader> reader;
    std::vector<Track> tracks;
    std::mutex gate;
    std::atomic<bool> closed = false;
    std::atomic<uint64_t> epoch = 0;
    SourceStatistics statistics;
    int64_t expectedAudio = -1;
    double timeScale = 1;
    int64_t duration = 0;
    weak_ref<MediaStreamSource> stream;

    Track const* Find(IMediaStreamDescriptor const& descriptor) const {
        for (auto const& track : tracks) if (track.descriptor == descriptor) return &track;
        return nullptr;
    }
};
struct Owner { std::shared_ptr<State> state; };

// The reader demuxes compressed video and uses the system audio decoder for PCM.
// The existing MediaPlayer retains video decoding, audio rendering and the clock.
void Initialize() {
    static std::once_flag once;
    std::call_once(once, [] { check_hresult(MFStartup(MF_VERSION)); });
}

bool IsSubtype(hstring const& actual, hstring const& expected) {
    // MF media types and WinRT constants use different casing (HEVC vs Hevc).
    return CompareStringOrdinal(actual.c_str(), static_cast<int>(actual.size()),
        expected.c_str(), static_cast<int>(expected.size()), TRUE) == CSTR_EQUAL;
}

void SetNames(IMFSourceReader* reader, DWORD index, IMediaStreamDescriptor const& descriptor) {
    auto audio = descriptor.try_as<AudioStreamDescriptor>();
    if (!audio) return;
    PROPVARIANT value{};
    if (SUCCEEDED(reader->GetPresentationAttribute(index, MF_SD_LANGUAGE, &value)) && value.vt == VT_LPWSTR)
        audio.Language(value.pwszVal);
    PropVariantClear(&value);
    if (SUCCEEDED(reader->GetPresentationAttribute(index, MF_SD_STREAM_NAME, &value)) && value.vt == VT_LPWSTR)
        audio.Name(value.pwszVal);
    PropVariantClear(&value);
}

// Complete deferrals even when an old request was flushed by seek or close.
template<class Deferral> struct Completion {
    Deferral value;
    ~Completion() noexcept { try { value.Complete(); } catch (...) {} }
};

void Attach(MediaStreamSource const& stream, std::shared_ptr<State> const& state) {
    stream.Closed([state](auto const&, auto const&) { state->closed = true; ++state->epoch; });
    stream.Starting([state](auto const& sender, MediaStreamSourceStartingEventArgs const& args) {
        auto request = args.Request();
        auto deferral = request.GetDeferral();
        auto epoch = ++state->epoch;
        winrt::Windows::System::Threading::ThreadPool::RunAsync([state, sender, request, deferral, epoch](auto const&) {
            Completion complete{deferral};
            try {
                std::lock_guard lock(state->gate);
                if (state->closed || state->epoch != epoch) return;
                if (auto position = request.StartPosition()) {
                    PROPVARIANT target{};
                    check_hresult(InitPropVariantFromInt64(static_cast<int64_t>(std::llround(position.Value().count() * state->timeScale)), &target));
                    auto result = state->reader->SetCurrentPosition(GUID_NULL, target);
                    PropVariantClear(&target);
                    check_hresult(result);
                    state->expectedAudio = -1;
                    state->statistics = {};
                    state->statistics.epoch = epoch;
                    for (auto& track : state->tracks) if (track.tempo) track.tempo->Reset(state->timeScale);
                    request.SetActualStartPosition(position.Value());
                }
            } catch (...) {
                if (!state->closed && state->epoch == epoch) sender.NotifyError(MediaStreamSourceErrorStatus::Other);
            }
        });
    });
    stream.SwitchStreamsRequested([state](auto const& sender, MediaStreamSourceSwitchStreamsRequestedEventArgs const& args) {
        auto request = args.Request();
        auto deferral = request.GetDeferral();
        winrt::Windows::System::Threading::ThreadPool::RunAsync([state, sender, request, deferral](auto const&) {
            Completion complete{deferral};
            try {
                std::lock_guard lock(state->gate);
                if (state->closed) return;
                if (auto old = state->Find(request.OldStreamDescriptor()))
                    check_hresult(state->reader->SetStreamSelection(old->index, FALSE));
                if (auto next = state->Find(request.NewStreamDescriptor()))
                    check_hresult(state->reader->SetStreamSelection(next->index, TRUE));
                state->expectedAudio = -1;
                for (auto& track : state->tracks) if (track.tempo) track.tempo->Reset(state->timeScale);
            } catch (...) {
                if (!state->closed) sender.NotifyError(MediaStreamSourceErrorStatus::Other);
            }
        });
    });
    stream.SampleRequested([state](auto const& sender, MediaStreamSourceSampleRequestedEventArgs const& args) {
        auto request = args.Request();
        auto deferral = request.GetDeferral();
        auto epoch = state->epoch.load();
        auto requestedAt = Clock::now();
        // ReadSample must not block a Media Foundation event/work queue.
        winrt::Windows::System::Threading::ThreadPool::RunAsync([state, sender, request, deferral, epoch, requestedAt](auto const&) {
            Completion complete{deferral};
            try {
                std::lock_guard lock(state->gate);
                if (state->closed || state->epoch != epoch) return;
                auto track = state->Find(request.StreamDescriptor());
                if (!track || !track->descriptor.IsSelected()) return;
                DWORD actual = 0, flags = 0;
                LONGLONG timestamp = 0;
                com_ptr<IMFSample> sample;
                MediaStreamSample result{nullptr};
                // A tempo processor may need a few input blocks before its compensated output is ready.
                while (!result) {
                    auto readingAt = Clock::now();
                    do {
                        check_hresult(state->reader->ReadSample(track->index, 0, &actual, &flags, &timestamp, sample.put()));
                        if (state->closed || state->epoch != epoch) return;
                        if (flags & MF_SOURCE_READERF_ERROR) throw_hresult(E_FAIL);
                        if (flags & MF_SOURCE_READERF_STREAMTICK) ++state->statistics.streamTicks;
                    } while (!sample && !(flags & MF_SOURCE_READERF_ENDOFSTREAM));
                    state->statistics.readMaxMs = (std::max)(state->statistics.readMaxMs, Milliseconds(readingAt));
                    if (!sample) {
                        if (!track->tempo) return;
                        auto tail = track->tempo->Finish();
                        if (tail.samples.empty()) return;
                        winrt::Windows::Storage::Streams::Buffer buffer{static_cast<uint32_t>(tail.samples.size() * sizeof(float))};
                        buffer.Length(buffer.Capacity());
                        BYTE* to = nullptr;
                        check_hresult(buffer.as<::Windows::Storage::Streams::IBufferByteAccess>()->Buffer(&to));
                        memcpy(to, tail.samples.data(), buffer.Length());
                        result = MediaStreamSample::CreateFromBuffer(buffer, TimeSpan(tail.timestamp));
                        result.Duration(TimeSpan(tail.duration));
                        break;
                    }
                    com_ptr<IMFMediaBuffer> bytes;
                    check_hresult(sample->ConvertToContiguousBuffer(bytes.put()));
                    BYTE* from = nullptr;
                    DWORD length = 0;
                    check_hresult(bytes->Lock(&from, nullptr, &length));
                    AudioTempo::Packet audioPacket{};
                    winrt::Windows::Storage::Streams::Buffer buffer{nullptr};
                    try {
                        if (track->tempo) {
                            audioPacket = track->tempo->Process(reinterpret_cast<float const*>(from), length / sizeof(float) / track->channels, timestamp);
                            length = static_cast<uint32_t>(audioPacket.samples.size() * sizeof(float));
                        }
                        buffer = winrt::Windows::Storage::Streams::Buffer{length};
                        buffer.Length(length);
                        BYTE* to = nullptr;
                        check_hresult(buffer.as<::Windows::Storage::Streams::IBufferByteAccess>()->Buffer(&to));
                        if (length) memcpy(to, track->tempo ? reinterpret_cast<BYTE const*>(audioPacket.samples.data()) : from, length);
                    } catch (...) { bytes->Unlock(); throw; }
                    check_hresult(bytes->Unlock());
                    LONGLONG duration = 0;
                    sample->GetSampleDuration(&duration);
                    if (!track->video) {
                        auto& stats = state->statistics;
                        ++stats.audioSamples;
                        stats.audioEnd = timestamp + duration;
                        auto gap = state->expectedAudio < 0 ? 0 : timestamp - state->expectedAudio;
                        if (gap > 10000) { ++stats.audioGaps; stats.maxAudioGap = (std::max)(stats.maxAudioGap, gap); }
                        state->expectedAudio = duration > 0 ? timestamp + duration : -1;
                    }
                    if (!length) { sample = nullptr; continue; }
                    result = MediaStreamSample::CreateFromBuffer(buffer, TimeSpan(track->tempo ? audioPacket.timestamp : static_cast<int64_t>(std::llround(timestamp / state->timeScale))));
                    result.Duration(TimeSpan(track->tempo ? audioPacket.duration : static_cast<int64_t>(std::llround(duration / state->timeScale))));
                    UINT32 flag = 0;
                    if (SUCCEEDED(sample->GetUINT32(MFSampleExtension_CleanPoint, &flag))) result.KeyFrame(flag != 0);
                    auto discontinuity = SUCCEEDED(sample->GetUINT32(MFSampleExtension_Discontinuity, &flag)) && flag != 0;
                    result.Discontinuous(discontinuity);
                    UINT64 dts = 0;
                    if (track->video) result.DecodeTimestamp(TimeSpan(static_cast<int64_t>(std::llround(
                        (SUCCEEDED(sample->GetUINT64(MFSampleExtension_DecodeTimestamp, &dts)) ? static_cast<int64_t>(dts) : timestamp) / state->timeScale))));
                }
                if (!state->closed && state->epoch == epoch) {
                    request.Sample(result);
                    auto& stats = state->statistics;
                    if (track->video) {
                        ++stats.videoSamples;
                        stats.videoTimestamp = timestamp;
                        stats.videoRequestMaxMs = (std::max)(stats.videoRequestMaxMs, Milliseconds(requestedAt));
                    } else {
                        stats.audioRequestMaxMs = (std::max)(stats.audioRequestMaxMs, Milliseconds(requestedAt));
                    }
                }
            } catch (...) {
                if (!state->closed && state->epoch == epoch) sender.NotifyError(MediaStreamSourceErrorStatus::Other);
            }
        });
    });
}
}

extern "C" __declspec(dllexport) HRESULT __stdcall OFPlayerCreateContinuousSource(
    wchar_t const* path, void** sourceResult, void** ownerResult) noexcept {
    if (!path || !sourceResult || !ownerResult) return E_POINTER;
    *sourceResult = *ownerResult = nullptr;
    try {
        Apartment apartment;
        Initialize();
        auto state = std::make_shared<State>();
        check_hresult(MFCreateSourceReaderFromURL(path, nullptr, state->reader.put()));
        check_hresult(state->reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
        IMediaStreamDescriptor video{nullptr}, audio{nullptr};
        for (DWORD index = 0; index < 256; ++index) {
            com_ptr<IMFMediaType> type;
            auto hr = state->reader->GetNativeMediaType(index, 0, type.put());
            if (hr == MF_E_INVALIDSTREAMNUMBER) break;
            check_hresult(hr);
            GUID major{};
            check_hresult(type->GetGUID(MF_MT_MAJOR_TYPE, &major));
            IMediaStreamDescriptor descriptor{nullptr};
            std::unique_ptr<AudioTempo> tempo;
            uint32_t channels = 0;
            if (major == MFMediaType_Video) {
                if (video) return S_FALSE; // Preserve alternate-video/attachment behavior through the system source.
                VideoEncodingProperties properties{nullptr};
                check_hresult(MFCreatePropertiesFromMediaType(type.get(), guid_of<VideoEncodingProperties>(), put_abi(properties)));
                auto subtype = properties.Subtype();
                if (IsSubtype(subtype, MediaEncodingSubtypes::H264())) properties.Subtype(MediaEncodingSubtypes::H264Es());
                else if (IsSubtype(subtype, MediaEncodingSubtypes::Hevc())) properties.Subtype(MediaEncodingSubtypes::HevcEs());
                else if (!IsSubtype(subtype, MediaEncodingSubtypes::H264Es()) &&
                    !IsSubtype(subtype, MediaEncodingSubtypes::HevcEs())) return S_FALSE;
                // Source Reader supplies Annex-B samples. H264/HEVC without ES describes
                // a different framing contract and can buffer indefinitely in MSS.
                video = descriptor = VideoStreamDescriptor(properties);
            } else if (major == MFMediaType_Audio) {
                com_ptr<IMFMediaType> pcm;
                check_hresult(MFCreateMediaType(pcm.put()));
                check_hresult(pcm->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
                check_hresult(pcm->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_Float));
                check_hresult(state->reader->SetCurrentMediaType(index, nullptr, pcm.get()));
                type = nullptr;
                check_hresult(state->reader->GetCurrentMediaType(index, type.put()));
                AudioEncodingProperties properties{nullptr};
                check_hresult(MFCreatePropertiesFromMediaType(type.get(), guid_of<AudioEncodingProperties>(), put_abi(properties)));
                channels = properties.ChannelCount();
                tempo = std::make_unique<AudioTempo>();
                tempo->Configure(static_cast<int>(channels), static_cast<int>(properties.SampleRate()));
                descriptor = AudioStreamDescriptor(properties);
                if (!audio) audio = descriptor;
            } else return S_FALSE; // Keep embedded text/data tracks on the original source.
            check_hresult(state->reader->SetCurrentMediaType(index, nullptr, type.get()));
            SetNames(state->reader.get(), index, descriptor);
            state->tracks.push_back({index, descriptor, major == MFMediaType_Video, channels, std::move(tempo)});
        }
        if (!video) return S_FALSE;
        MediaStreamSource stream = audio ? MediaStreamSource(video, audio) : MediaStreamSource(video);
        for (auto const& track : state->tracks) {
            if (track.descriptor != video && track.descriptor != audio) stream.AddStreamDescriptor(track.descriptor);
            if (track.descriptor == video || track.descriptor == audio)
                check_hresult(state->reader->SetStreamSelection(track.index, TRUE));
        }
        PROPVARIANT duration{};
        check_hresult(state->reader->GetPresentationAttribute(static_cast<DWORD>(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION, &duration));
        stream.Duration(TimeSpan(static_cast<int64_t>(duration.uhVal.QuadPart)));
        state->duration = static_cast<int64_t>(duration.uhVal.QuadPart);
        state->stream = stream;
        PropVariantClear(&duration);
        stream.CanSeek(true);
        // Local reads are demand driven. A positive startup buffer makes MSS
        // repeatedly rebuffer as the playback clock advances faster at 4x.
        stream.BufferTime(TimeSpan::zero());
        stream.MaxSupportedPlaybackRate(box_value(4.0).as<winrt::Windows::Foundation::IReference<double>>());
        Attach(stream, state);
        auto source = MediaSource::CreateFromMediaStreamSource(stream);
        auto owner = std::make_unique<Owner>();
        owner->state = std::move(state);
        *sourceResult = detach_abi(source);
        *ownerResult = owner.release();
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" __declspec(dllexport) void __stdcall OFPlayerCloseContinuousSource(void* handle) noexcept {
    std::unique_ptr<Owner> owner(static_cast<Owner*>(handle));
    if (owner) { owner->state->closed = true; ++owner->state->epoch; }
}

extern "C" __declspec(dllexport) HRESULT __stdcall OFPlayerReadContinuousStatistics(void* handle, SourceStatistics* result) noexcept {
    if (!handle || !result) return E_POINTER;
    auto& state = *static_cast<Owner*>(handle)->state;
    std::unique_lock lock(state.gate, std::try_to_lock);
    if (!lock.owns_lock() || state.closed) return S_FALSE; // Never stall the playback/UI thread for diagnostics.
    *result = state.statistics;
    result->duration = state.duration;
    state.statistics.audioRequestMaxMs = state.statistics.videoRequestMaxMs = state.statistics.readMaxMs = 0;
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT __stdcall OFPlayerSetContinuousRate(void* handle, double rate) noexcept {
    if (!handle || !std::isfinite(rate) || rate < 0.25 || rate > 4) return E_INVALIDARG;
    try {
        auto& state = *static_cast<Owner*>(handle)->state;
        std::lock_guard lock(state.gate);
        if (state.closed) return RO_E_CLOSED;
        double scale = (std::max)(1.0, rate / 3.0);
        if (state.timeScale == scale) return S_OK;
        auto stream = state.stream.get();
        if (!stream) return RO_E_CLOSED;
        stream.Duration(TimeSpan(static_cast<int64_t>(std::llround(state.duration / scale))));
        ++state.epoch;
        state.timeScale = scale;
        state.expectedAudio = -1;
        for (auto& track : state.tracks) if (track.tempo) track.tempo->Reset(scale);
        return S_OK;
    } catch (...) { return to_hresult(); }
}
