#pragma once
#pragma warning(push, 0)
#include "ThirdParty/signalsmith-stretch.h"
#pragma warning(pop)
#include <cstdint>
#include <cmath>
#include <vector>

// Small residual tempo correction above the system transport's 3x ceiling.
// PCM stays at its actual sample rate; output length and timestamps agree.
class AudioTempo {
    signalsmith::stretch::SignalsmithStretch<float> stretch{0};
    int channels = 0, sampleRate = 0;
    double scale = 1;
    int64_t inputFrames = 0, plannedFrames = 0, deliveredFrames = 0, discard = 0;
    int64_t origin = -1;
    bool finished = false;
    bool started = false;
    std::vector<std::vector<float>> input, output;

    std::vector<float> Take(int count) {
        int skip = static_cast<int>((std::min)(discard, static_cast<int64_t>(count)));
        discard -= skip;
        count -= skip;
        count = static_cast<int>((std::min)(static_cast<int64_t>(count), plannedFrames - deliveredFrames));
        std::vector<float> result(static_cast<size_t>(count) * channels);
        for (int c = 0; c < channels; ++c)
            for (int i = 0; i < count; ++i) result[static_cast<size_t>(i) * channels + c] = output[c][skip + i];
        return result;
    }
public:
    struct Packet { std::vector<float> samples; int64_t timestamp, duration; };
    void Configure(int nChannels, int rate) {
        channels = nChannels; sampleRate = rate;
        stretch.presetDefault(channels, static_cast<float>(sampleRate));
        input.resize(channels); output.resize(channels);
        Reset(1);
    }
    void Reset(double ratio) {
        scale = ratio; inputFrames = plannedFrames = deliveredFrames = 0;
        origin = -1; finished = started = false;
        stretch.reset();
        discard = ratio == 1 ? 0 : static_cast<int64_t>(std::llround(stretch.inputLatency() / ratio)) + stretch.outputLatency();
    }
    Packet Wrap(std::vector<float> samples) {
        int64_t count = static_cast<int64_t>(samples.size() / channels);
        auto start = static_cast<int64_t>(std::llround(origin / scale)) + deliveredFrames * 10000000 / sampleRate;
        auto end = static_cast<int64_t>(std::llround(origin / scale)) + (deliveredFrames + count) * 10000000 / sampleRate;
        deliveredFrames += count;
        return {std::move(samples), start, end - start};
    }
    Packet Process(float const* samples, size_t count, int64_t timestamp) {
        if (!started) { origin = timestamp; started = true; }
        if (scale == 1) {
            inputFrames += static_cast<int64_t>(count);
            plannedFrames = inputFrames;
            deliveredFrames += static_cast<int64_t>(count);
            return {std::vector<float>(samples, samples + count * channels), timestamp, static_cast<int64_t>(count) * 10000000 / sampleRate};
        }
        auto nextFrames = static_cast<int64_t>(std::llround((inputFrames + count) / scale));
        int nOutput = static_cast<int>(nextFrames - plannedFrames);
        for (int c = 0; c < channels; ++c) {
            input[c].resize(count); output[c].resize(nOutput);
            for (size_t i = 0; i < count; ++i) input[c][i] = samples[i * channels + c];
        }
        stretch.process(input, static_cast<int>(count), output, nOutput);
        inputFrames += static_cast<int64_t>(count);
        plannedFrames = nextFrames;
        return Wrap(Take(nOutput));
    }
    Packet Finish() {
        if (finished || !started || scale == 1) return {{}, 0, 0};
        finished = true;
        int padding = stretch.inputLatency();
        int count = static_cast<int>(std::llround(padding / scale));
        for (int c = 0; c < channels; ++c) { input[c].assign(padding, 0); output[c].resize(count); }
        stretch.process(input, padding, output, count);
        auto samples = Take(count);
        auto delivered = Wrap(std::move(samples));
        count = stretch.outputLatency();
        for (auto& channel : output) channel.resize(count);
        stretch.flush(output, count);
        auto tail = Wrap(Take(count));
        delivered.samples.insert(delivered.samples.end(), tail.samples.begin(), tail.samples.end());
        delivered.duration += tail.duration;
        return delivered;
    }
};
