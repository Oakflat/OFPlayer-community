use cpal::{self, traits::DeviceTrait, traits::HostTrait};
use rodio::{source::SeekError, Decoder, OutputStream, OutputStreamBuilder, Sink, Source};
use rustfft::{num_complex::Complex, Fft, FftPlanner};
use serde::{Deserialize, Serialize};
use std::{
    fs::File,
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, AtomicU32, AtomicU64, AtomicU8, AtomicUsize, Ordering},
        Arc,
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use crate::{
    audio_formats,
    dsd_playback::DsdPcmSource,
    system_media::{SystemMediaMetadata, SystemMediaSession, SystemPlaybackStatus},
};

const AUDIO_METER_LEVEL_COUNT: usize = 8;
const AUDIO_METER_FFT_SIZE: usize = 2048;
const AUDIO_METER_HOP_SIZE: usize = 1024;
const AUDIO_METER_RING_CAPACITY: usize = AUDIO_METER_FFT_SIZE * 8;
const AUDIO_METER_STALE_AFTER_MS: u64 = 700;
const AUDIO_METER_WORKER_IDLE_SLEEP_MS: u64 = 4;
const AUDIO_METER_GAIN: f32 = 34.0;
const AUDIO_METER_NOISE_FLOOR: f32 = 0.018;
const AUDIO_METER_ATTACK: f32 = 0.58;
const AUDIO_METER_RELEASE: f32 = 0.18;
const AUDIO_METER_BANDS_HZ: [(f32, f32); AUDIO_METER_LEVEL_COUNT] = [
    (60.0, 120.0),
    (120.0, 250.0),
    (250.0, 500.0),
    (500.0, 1_000.0),
    (1_000.0, 2_000.0),
    (2_000.0, 4_000.0),
    (4_000.0, 8_000.0),
    (8_000.0, 12_000.0),
];
const AUDIO_METER_BAND_WEIGHTS: [f32; AUDIO_METER_LEVEL_COUNT] =
    [1.45, 1.35, 0.84, 0.78, 0.96, 1.2, 1.34, 1.08];

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum PlaybackStatus {
    Idle,
    Paused,
    Playing,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaybackSnapshot {
    pub status: PlaybackStatus,
    pub active_track_id: Option<String>,
    pub current_time: f64,
    pub duration: f64,
    pub volume: f64,
    pub ended_counter: u64,
    pub ended_track_id: Option<String>,
    pub error: Option<String>,
    pub backend: &'static str,
    pub signal_path: Option<PlaybackSignalPath>,
    pub audio_levels: Vec<f32>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LoadTrackRequest {
    pub track_id: String,
    pub path: String,
    pub autoplay: bool,
    pub start_time: Option<f64>,
    pub duration_hint: Option<f64>,
    pub sample_rate: Option<u32>,
    pub bit_depth: Option<u32>,
    pub volume: Option<f64>,
    pub delete_on_release: Option<bool>,
    pub media: Option<LoadTrackMediaMetadata>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SeekRequest {
    pub seconds: f64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VolumeRequest {
    pub volume: f64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OutputDevicePreferenceRequest {
    pub device_id: Option<String>,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LoadTrackMediaMetadata {
    pub title: Option<String>,
    pub artist: Option<String>,
    pub album: Option<String>,
    pub cover_url: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaybackOutputDevice {
    pub id: String,
    pub name: String,
    pub backend: String,
    pub backend_label: String,
    pub is_default: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaybackOutputDevicesSnapshot {
    pub devices: Vec<PlaybackOutputDevice>,
    pub preferred_device_id: Option<String>,
    pub active_device_id: Option<String>,
    pub active_device_name: Option<String>,
    pub prefers_system_default: bool,
    pub preferred_device_available: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaybackOutputDeviceChangeResult {
    pub playback: PlaybackSnapshot,
    pub devices: PlaybackOutputDevicesSnapshot,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaybackSignalFormat {
    pub sample_rate: u32,
    pub channels: u16,
    pub bit_depth: Option<u32>,
    pub sample_format: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaybackSignalPath {
    pub source: PlaybackSignalFormat,
    pub output: PlaybackSignalFormat,
    pub resampled: bool,
    pub channel_converted: bool,
    pub sample_format_converted: bool,
    pub software_mixer: bool,
    pub software_volume: bool,
    pub bit_perfect: bool,
    pub integrity_status: &'static str,
}

#[derive(Clone, Debug)]
struct LoadedTrack {
    id: String,
    path: PathBuf,
    duration: f64,
    sample_rate_hint: Option<u32>,
    bit_depth: Option<u32>,
    delete_on_release: bool,
    media: SystemMediaMetadata,
}

struct ResolvedOutputStream {
    stream: OutputStream,
    active_device_id: Option<String>,
    active_device_name: Option<String>,
    warning: Option<String>,
}

struct PreparedPlaybackSource {
    source: Box<dyn Source + Send>,
    source_sample_rate: u32,
    source_channels: u16,
    decoded_duration: f64,
    source_bit_depth: Option<u32>,
    source_sample_format: String,
    supports_seek: bool,
    warning: Option<String>,
}

#[derive(Clone, Debug)]
pub(crate) struct AudioMeter {
    inner: Arc<AudioMeterState>,
}

#[derive(Debug)]
struct AudioMeterState {
    levels: [AtomicU8; AUDIO_METER_LEVEL_COUNT],
    last_updated_ms: AtomicU64,
    sample_rate: AtomicU32,
    playing: AtomicBool,
    volume: AtomicU8,
    ring: MeterSampleRing,
}

#[derive(Debug)]
struct MeterSampleRing {
    read_index: AtomicUsize,
    write_index: AtomicUsize,
    samples: Box<[AtomicU32]>,
}

impl MeterSampleRing {
    fn new(capacity: usize) -> Self {
        let samples = (0..capacity)
            .map(|_| AtomicU32::new(0))
            .collect::<Vec<_>>()
            .into_boxed_slice();

        Self {
            read_index: AtomicUsize::new(0),
            write_index: AtomicUsize::new(0),
            samples,
        }
    }

    fn capacity(&self) -> usize {
        self.samples.len()
    }

    fn try_push(&self, sample: f32) -> bool {
        let write_index = self.write_index.load(Ordering::Relaxed);
        let next_write_index = (write_index + 1) % self.capacity();
        let read_index = self.read_index.load(Ordering::Acquire);

        if next_write_index == read_index {
            return false;
        }

        self.samples[write_index].store(sample.to_bits(), Ordering::Relaxed);
        self.write_index.store(next_write_index, Ordering::Release);
        true
    }

    fn pop(&self) -> Option<f32> {
        let read_index = self.read_index.load(Ordering::Relaxed);
        let write_index = self.write_index.load(Ordering::Acquire);

        if read_index == write_index {
            return None;
        }

        let bits = self.samples[read_index].load(Ordering::Relaxed);
        self.read_index
            .store((read_index + 1) % self.capacity(), Ordering::Release);
        Some(f32::from_bits(bits))
    }

    fn clear(&self) {
        let write_index = self.write_index.load(Ordering::Acquire);
        self.read_index.store(write_index, Ordering::Release);
    }
}

impl AudioMeterState {
    fn new() -> Self {
        Self {
            levels: std::array::from_fn(|_| AtomicU8::new(0)),
            last_updated_ms: AtomicU64::new(0),
            sample_rate: AtomicU32::new(0),
            playing: AtomicBool::new(false),
            volume: AtomicU8::new(level_to_u8(0.8)),
            ring: MeterSampleRing::new(AUDIO_METER_RING_CAPACITY),
        }
    }
}

impl Default for AudioMeter {
    fn default() -> Self {
        let meter = Self {
            inner: Arc::new(AudioMeterState::new()),
        };
        spawn_audio_meter_worker(meter.inner.clone());
        meter
    }
}

impl AudioMeter {
    fn configure(&self, sample_rate: u32) {
        self.inner.sample_rate.store(sample_rate, Ordering::Release);
        self.inner.ring.clear();
    }

    fn push_sample(&self, sample: f32) {
        let _ = self.inner.ring.try_push(sample);
    }

    fn push_levels(&self, levels: [f32; AUDIO_METER_LEVEL_COUNT]) {
        for (index, target) in levels.into_iter().enumerate() {
            let current = self.inner.levels[index].load(Ordering::Relaxed) as f32 / 255.0;
            let coefficient = if target > current {
                AUDIO_METER_ATTACK
            } else {
                AUDIO_METER_RELEASE
            };
            let next = current + (target.clamp(0.0, 1.0) - current) * coefficient;
            self.inner.levels[index].store(level_to_u8(next), Ordering::Relaxed);
        }

        self.inner
            .last_updated_ms
            .store(audio_meter_now_ms(), Ordering::Release);
    }

    fn reset(&self) {
        for level in &self.inner.levels {
            level.store(0, Ordering::Relaxed);
        }

        self.inner.last_updated_ms.store(0, Ordering::Release);
        self.inner.ring.clear();
    }

    fn set_playback_state(&self, playing: bool, volume: f32) {
        self.inner.playing.store(playing, Ordering::Release);
        self.inner
            .volume
            .store(level_to_u8(volume), Ordering::Release);
    }

    pub(crate) fn shared_snapshot(&self) -> Vec<f32> {
        let volume = self.inner.volume.load(Ordering::Acquire) as f32 / 255.0;
        let playing = self.inner.playing.load(Ordering::Acquire);

        self.snapshot(volume, playing)
    }

    fn snapshot(&self, volume: f32, playing: bool) -> Vec<f32> {
        let last_updated_ms = self.inner.last_updated_ms.load(Ordering::Acquire);
        let is_stale = last_updated_ms == 0
            || audio_meter_now_ms().saturating_sub(last_updated_ms) > AUDIO_METER_STALE_AFTER_MS;

        if !playing || is_stale {
            return vec![0.0; AUDIO_METER_LEVEL_COUNT];
        }

        self.inner
            .levels
            .iter()
            .map(|level| ((level.load(Ordering::Relaxed) as f32 / 255.0) * volume).clamp(0.0, 1.0))
            .collect()
    }
}

fn spawn_audio_meter_worker(inner: Arc<AudioMeterState>) {
    let _ = thread::Builder::new()
        .name(String::from("ofplayer-meter-worker"))
        .spawn(move || {
            let meter = AudioMeter {
                inner: inner.clone(),
            };
            let mut analyzer_sample_rate = 0_u32;
            let mut analyzer = SpectrumAnalyzer::new(48_000);

            loop {
                let sample_rate = inner.sample_rate.load(Ordering::Acquire);

                if sample_rate == 0 {
                    thread::sleep(Duration::from_millis(AUDIO_METER_WORKER_IDLE_SLEEP_MS));
                    continue;
                }

                if sample_rate != analyzer_sample_rate {
                    analyzer = SpectrumAnalyzer::new(sample_rate);
                    analyzer_sample_rate = sample_rate;
                    inner.ring.clear();
                }

                let mut processed_samples = 0usize;

                while let Some(sample) = inner.ring.pop() {
                    if let Some(levels) = analyzer.push_mono_sample(sample) {
                        meter.push_levels(levels);
                    }

                    processed_samples += 1;

                    if processed_samples >= AUDIO_METER_RING_CAPACITY {
                        break;
                    }
                }

                if processed_samples == 0 {
                    thread::sleep(Duration::from_millis(AUDIO_METER_WORKER_IDLE_SLEEP_MS));
                }
            }
        });
}

fn level_to_u8(level: f32) -> u8 {
    (level.clamp(0.0, 1.0) * 255.0).round().clamp(0.0, 255.0) as u8
}

fn audio_meter_now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis().min(u128::from(u64::MAX)) as u64)
        .unwrap_or_default()
}

struct SpectrumAnalyzer {
    sample_rate: u32,
    write_index: usize,
    filled_samples: usize,
    samples_since_analysis: usize,
    window_samples: Vec<f32>,
    fft_buffer: Vec<Complex<f32>>,
    fft: Arc<dyn Fft<f32>>,
}

impl SpectrumAnalyzer {
    fn new(sample_rate: u32) -> Self {
        let mut planner = FftPlanner::<f32>::new();
        let fft = planner.plan_fft_forward(AUDIO_METER_FFT_SIZE);

        Self {
            sample_rate,
            write_index: 0,
            filled_samples: 0,
            samples_since_analysis: 0,
            window_samples: vec![0.0; AUDIO_METER_FFT_SIZE],
            fft_buffer: vec![Complex::new(0.0, 0.0); AUDIO_METER_FFT_SIZE],
            fft,
        }
    }

    fn push_mono_sample(&mut self, sample: f32) -> Option<[f32; AUDIO_METER_LEVEL_COUNT]> {
        self.window_samples[self.write_index] = sample;
        self.write_index = (self.write_index + 1) % AUDIO_METER_FFT_SIZE;
        self.filled_samples = (self.filled_samples + 1).min(AUDIO_METER_FFT_SIZE);
        self.samples_since_analysis += 1;

        if self.filled_samples < AUDIO_METER_FFT_SIZE
            || self.samples_since_analysis < AUDIO_METER_HOP_SIZE
        {
            return None;
        }

        self.samples_since_analysis = 0;
        Some(self.analyze())
    }

    fn analyze(&mut self) -> [f32; AUDIO_METER_LEVEL_COUNT] {
        for index in 0..AUDIO_METER_FFT_SIZE {
            let source_index = (self.write_index + index) % AUDIO_METER_FFT_SIZE;
            let window_position = index as f32 / (AUDIO_METER_FFT_SIZE - 1) as f32;
            let hann = 0.5 - 0.5 * (std::f32::consts::TAU * window_position).cos();
            self.fft_buffer[index] = Complex::new(self.window_samples[source_index] * hann, 0.0);
        }

        self.fft.process(&mut self.fft_buffer);

        let mut levels = [0.0; AUDIO_METER_LEVEL_COUNT];
        for (index, (low_hz, high_hz)) in AUDIO_METER_BANDS_HZ.iter().copied().enumerate() {
            levels[index] = self.band_level(low_hz, high_hz, AUDIO_METER_BAND_WEIGHTS[index]);
        }

        levels
    }

    fn band_level(&self, low_hz: f32, high_hz: f32, weight: f32) -> f32 {
        let Some((start_bin, end_bin)) =
            frequency_bin_range(self.sample_rate, AUDIO_METER_FFT_SIZE, low_hz, high_hz)
        else {
            return 0.0;
        };

        let mut power_sum = 0.0;
        let mut bin_count = 0usize;

        for bin in start_bin..end_bin {
            let magnitude = self.fft_buffer[bin].norm() / AUDIO_METER_FFT_SIZE as f32;
            power_sum += magnitude * magnitude;
            bin_count += 1;
        }

        if bin_count == 0 {
            return 0.0;
        }

        normalize_band_magnitude((power_sum / bin_count as f32).sqrt(), weight)
    }
}

fn frequency_bin_range(
    sample_rate: u32,
    fft_size: usize,
    low_hz: f32,
    high_hz: f32,
) -> Option<(usize, usize)> {
    if sample_rate == 0 || fft_size < 2 || low_hz >= high_hz {
        return None;
    }

    let nyquist = sample_rate as f32 / 2.0;
    let low_hz = low_hz.clamp(0.0, nyquist);
    let high_hz = high_hz.clamp(0.0, nyquist);
    if low_hz >= high_hz {
        return None;
    }

    let hz_per_bin = sample_rate as f32 / fft_size as f32;
    let max_bin_exclusive = fft_size / 2;
    if max_bin_exclusive <= 1 {
        return None;
    }

    let start_bin = ((low_hz / hz_per_bin).ceil() as usize).clamp(1, max_bin_exclusive - 1);
    let end_bin =
        ((high_hz / hz_per_bin).floor() as usize + 1).clamp(start_bin + 1, max_bin_exclusive);

    Some((start_bin, end_bin))
}

fn normalize_band_magnitude(magnitude: f32, weight: f32) -> f32 {
    let gated = (magnitude * AUDIO_METER_GAIN * weight - AUDIO_METER_NOISE_FLOOR).max(0.0);

    gated.clamp(0.0, 1.0).powf(0.52)
}

struct MeteredSource<S> {
    inner: S,
    meter: AudioMeter,
    channels: usize,
    channel_index: usize,
    frame_sum: f32,
}

impl<S> MeteredSource<S>
where
    S: Source,
{
    fn new(inner: S, meter: AudioMeter) -> Self {
        meter.configure(inner.sample_rate());

        Self {
            channels: usize::from(inner.channels()).max(1),
            inner,
            meter,
            channel_index: 0,
            frame_sum: 0.0,
        }
    }

    fn record_sample(&mut self, sample: f32) {
        self.frame_sum += sample;
        self.channel_index += 1;

        if self.channel_index < self.channels {
            return;
        }

        let mono_sample = self.frame_sum / self.channels as f32;
        self.channel_index = 0;
        self.frame_sum = 0.0;
        self.meter.push_sample(mono_sample);
    }

    fn reset_frame_state(&mut self) {
        self.channels = usize::from(self.inner.channels()).max(1);
        self.channel_index = 0;
        self.frame_sum = 0.0;
        self.meter.reset();
        self.meter.configure(self.inner.sample_rate());
    }
}

impl<S> Iterator for MeteredSource<S>
where
    S: Source,
{
    type Item = f32;

    fn next(&mut self) -> Option<Self::Item> {
        let sample = self.inner.next()?;
        self.record_sample(sample);
        Some(sample)
    }
}

impl<S> Source for MeteredSource<S>
where
    S: Source,
{
    fn current_span_len(&self) -> Option<usize> {
        self.inner.current_span_len()
    }

    fn channels(&self) -> u16 {
        self.inner.channels()
    }

    fn sample_rate(&self) -> u32 {
        self.inner.sample_rate()
    }

    fn total_duration(&self) -> Option<Duration> {
        self.inner.total_duration()
    }

    fn try_seek(&mut self, pos: Duration) -> Result<(), SeekError> {
        self.inner.try_seek(pos)?;
        self.reset_frame_state();
        Ok(())
    }
}

pub struct PlaybackManager {
    output_stream: Option<OutputStream>,
    sink: Option<Sink>,
    active_track: Option<LoadedTrack>,
    status: PlaybackStatus,
    current_time: f64,
    playback_anchor_time: f64,
    playback_anchor_started_at: Option<Instant>,
    volume: f32,
    ended_counter: u64,
    ended_track_id: Option<String>,
    error: Option<String>,
    signal_path: Option<PlaybackSignalPath>,
    audio_meter: AudioMeter,
    preferred_output_device_id: Option<String>,
    active_output_device_id: Option<String>,
    active_output_device_name: Option<String>,
    system_media: SystemMediaSession,
}

impl PlaybackManager {
    pub fn new() -> Self {
        Self {
            output_stream: None,
            sink: None,
            active_track: None,
            status: PlaybackStatus::Idle,
            current_time: 0.0,
            playback_anchor_time: 0.0,
            playback_anchor_started_at: None,
            volume: 0.8,
            ended_counter: 0,
            ended_track_id: None,
            error: None,
            signal_path: None,
            audio_meter: AudioMeter::default(),
            preferred_output_device_id: None,
            active_output_device_id: None,
            active_output_device_name: None,
            system_media: SystemMediaSession::default(),
        }
    }

    pub fn initialize_system_media(&mut self, app: &tauri::AppHandle) -> Result<(), String> {
        self.system_media.initialize(app)?;
        self.sync_system_media_session();
        Ok(())
    }

    pub fn snapshot(&mut self) -> PlaybackSnapshot {
        self.sync_runtime_state();
        self.sync_system_media_session();

        PlaybackSnapshot {
            status: self.status,
            active_track_id: self.active_track.as_ref().map(|track| track.id.clone()),
            current_time: self.current_time,
            duration: self
                .active_track
                .as_ref()
                .map(|track| track.duration)
                .unwrap_or(0.0),
            volume: self.volume as f64,
            ended_counter: self.ended_counter,
            ended_track_id: self.ended_track_id.clone(),
            error: self.error.clone(),
            backend: "rust",
            signal_path: self.signal_path.clone(),
            audio_levels: self.audio_levels(),
        }
    }

    pub fn load_track(&mut self, request: LoadTrackRequest) -> Result<PlaybackSnapshot, String> {
        let media = build_system_media_metadata(&request);
        let path = PathBuf::from(request.path.clone());

        if !path.is_file() {
            return self.fail("Selected track path is not available on disk.");
        }

        let previous_track = self.active_track.take();
        self.release_output();
        cleanup_released_track(previous_track);

        self.volume = clamp_volume(request.volume.unwrap_or(self.volume as f64));
        let track_id = request.track_id.clone();
        let duration_hint = clamp_time(request.duration_hint.unwrap_or(0.0));

        self.active_track = Some(LoadedTrack {
            id: track_id,
            path,
            duration: duration_hint,
            sample_rate_hint: request.sample_rate.filter(|value| *value > 0),
            bit_depth: request.bit_depth.filter(|value| *value > 0),
            delete_on_release: request.delete_on_release.unwrap_or(false),
            media,
        });
        self.ended_track_id = None;
        self.error = None;
        self.signal_path = None;
        self.reset_audio_meter();
        self.current_time = clamp_time(request.start_time.unwrap_or(0.0));

        self.rebuild_sink(self.current_time, request.autoplay)?;
        Ok(self.snapshot())
    }

    pub fn play(&mut self) -> Result<PlaybackSnapshot, String> {
        if self.active_track.is_none() {
            return self.fail("No track has been loaded into the Rust playback backend.");
        }

        self.sync_runtime_state();
        let should_restart = self.sink.is_none() || self.track_has_completed();

        if should_restart {
            let restart_at = if self.track_has_completed() {
                0.0
            } else {
                self.current_time
            };
            self.rebuild_sink(restart_at, true)?;
        } else if let Some(sink) = &self.sink {
            sink.play();
            self.set_runtime_position(self.current_time, true);
            self.error = None;
            self.ended_track_id = None;
        }

        Ok(self.snapshot())
    }

    pub fn pause(&mut self) -> PlaybackSnapshot {
        self.sync_runtime_state();

        if let Some(sink) = &self.sink {
            sink.pause();
        }

        self.set_runtime_position(self.current_time, false);
        self.snapshot()
    }

    pub fn seek(&mut self, request: SeekRequest) -> Result<PlaybackSnapshot, String> {
        let Some(active_track) = self.active_track.clone() else {
            return self.fail("Cannot seek because no track is loaded.");
        };

        let max_seek_time = if active_track.duration > 0.0 {
            active_track.duration
        } else {
            clamp_time(request.seconds)
        };
        let safe_seconds = clamp_time(request.seconds).min(max_seek_time);
        let should_autoplay = self.status == PlaybackStatus::Playing;

        match &self.sink {
            Some(sink) => {
                if sink
                    .try_seek(Duration::from_secs_f64(safe_seconds))
                    .is_err()
                {
                    self.rebuild_sink(safe_seconds, should_autoplay)?;
                } else {
                    self.set_runtime_position(safe_seconds, should_autoplay);

                    if safe_seconds < active_track.duration {
                        self.ended_track_id = None;
                    }
                }
            }
            _ => {
                self.rebuild_sink(safe_seconds, should_autoplay)?;
            }
        }

        Ok(self.snapshot())
    }

    pub fn set_volume(&mut self, request: VolumeRequest) -> PlaybackSnapshot {
        self.volume = clamp_volume(request.volume);
        self.audio_meter
            .set_playback_state(self.status == PlaybackStatus::Playing, self.volume);

        if let Some(sink) = &self.sink {
            sink.set_volume(self.volume);
        }

        self.snapshot()
    }

    pub fn reset(&mut self) -> PlaybackSnapshot {
        let previous_track = self.active_track.take();
        self.release_output();
        cleanup_released_track(previous_track);

        self.status = PlaybackStatus::Idle;
        self.current_time = 0.0;
        self.playback_anchor_time = 0.0;
        self.playback_anchor_started_at = None;
        self.ended_track_id = None;
        self.error = None;
        self.signal_path = None;
        self.reset_audio_meter();
        self.snapshot()
    }

    pub fn recover_output(&mut self) -> Result<PlaybackSnapshot, String> {
        self.sync_runtime_state();

        if self.active_track.is_none() {
            self.release_output();
            self.error = None;
            return Ok(self.snapshot());
        }

        let resume_position = self.current_time;
        let should_autoplay = self.status == PlaybackStatus::Playing;
        self.release_output();
        self.rebuild_sink(resume_position, should_autoplay)?;
        Ok(self.snapshot())
    }

    pub fn output_devices(&self) -> Result<PlaybackOutputDevicesSnapshot, String> {
        build_output_devices_snapshot(
            self.preferred_output_device_id.clone(),
            self.active_output_device_id.clone(),
            self.active_output_device_name.clone(),
        )
    }

    pub fn set_output_device_preference(
        &mut self,
        request: OutputDevicePreferenceRequest,
    ) -> Result<PlaybackOutputDeviceChangeResult, String> {
        self.sync_runtime_state();
        self.preferred_output_device_id = normalize_output_device_id(request.device_id);

        let resume_position = self.current_time;
        let should_autoplay = self.status == PlaybackStatus::Playing;
        let has_loaded_track = self.active_track.is_some();

        self.release_output();

        if has_loaded_track {
            self.rebuild_sink(resume_position, should_autoplay)?;
        } else {
            self.error = None;
        }

        let playback = self.snapshot();
        let devices = self.output_devices()?;

        Ok(PlaybackOutputDeviceChangeResult { playback, devices })
    }

    fn fail<T>(&mut self, message: impl Into<String>) -> Result<T, String> {
        let error = message.into();
        self.error = Some(error.clone());
        Err(error)
    }

    fn sync_runtime_state(&mut self) {
        let Some(active_track) = &self.active_track else {
            self.status = PlaybackStatus::Idle;
            self.current_time = 0.0;
            self.playback_anchor_time = 0.0;
            self.playback_anchor_started_at = None;
            return;
        };

        if self.status == PlaybackStatus::Playing {
            let elapsed_seconds = self
                .playback_anchor_started_at
                .map(|started_at| started_at.elapsed().as_secs_f64())
                .unwrap_or(0.0);
            let runtime_position = clamp_time(self.playback_anchor_time + elapsed_seconds);
            self.current_time = if active_track.duration > 0.0 {
                runtime_position.min(active_track.duration)
            } else {
                runtime_position
            };

            if active_track.duration > 0.0 && self.current_time >= active_track.duration {
                self.current_time = active_track.duration;
                self.playback_anchor_time = self.current_time;
                self.playback_anchor_started_at = None;
                self.status = PlaybackStatus::Paused;
                self.sink = None;

                if self.ended_track_id.as_deref() != Some(active_track.id.as_str()) {
                    self.ended_counter += 1;
                    self.ended_track_id = Some(active_track.id.clone());
                }
            }
        } else {
            self.current_time = if active_track.duration > 0.0 {
                self.playback_anchor_time.min(active_track.duration)
            } else {
                self.playback_anchor_time
            };

            if self.status != PlaybackStatus::Paused {
                self.status = PlaybackStatus::Paused;
            }
        }
    }

    fn ensure_output_stream(&mut self) -> Result<Option<String>, String> {
        if self.output_stream.is_some() {
            return Ok(None);
        }

        let resolved_stream = resolve_output_stream(self.preferred_output_device_id.as_deref())?;

        self.active_output_device_id = resolved_stream.active_device_id;
        self.active_output_device_name = resolved_stream.active_device_name;
        self.output_stream = Some(resolved_stream.stream);
        Ok(resolved_stream.warning)
    }

    fn rebuild_sink(&mut self, start_seconds: f64, autoplay: bool) -> Result<(), String> {
        let profile_started_at = Instant::now();
        let mut step_started_at = Instant::now();
        let output_warning = self.ensure_output_stream()?;
        debug_playback_log_step("ensure_output_stream", step_started_at, profile_started_at);

        let Some(active_track) = self.active_track.clone() else {
            return self.fail("Cannot build a playback sink without an active track.");
        };

        let file_size = active_track
            .path
            .metadata()
            .map(|metadata| metadata.len())
            .unwrap_or_default();
        step_started_at = Instant::now();
        let PreparedPlaybackSource {
            source,
            source_sample_rate,
            source_channels,
            decoded_duration,
            source_bit_depth,
            source_sample_format,
            supports_seek,
            warning: source_warning,
        } = prepare_playback_source(&active_track)?;
        let mut playback_warning = append_optional_warning(output_warning, source_warning);
        debug_playback_log_step("prepare_source", step_started_at, profile_started_at);

        let output_stream = self
            .output_stream
            .as_ref()
            .ok_or_else(|| String::from("Rust playback output stream is unavailable."))?;
        step_started_at = Instant::now();
        let output_format = playback_output_format(output_stream);
        let signal_path = build_signal_path(
            PlaybackSignalFormat {
                sample_rate: active_track.sample_rate_hint.unwrap_or(source_sample_rate),
                channels: source_channels,
                bit_depth: active_track.bit_depth.or(source_bit_depth),
                sample_format: Some(source_sample_format),
            },
            output_format,
            self.volume,
        );
        let mixer = output_stream.mixer();
        let sink = Sink::connect_new(mixer);
        let metered_source = MeteredSource::new(source, self.audio_meter.clone());

        sink.pause();
        sink.set_volume(self.volume);
        sink.append(metered_source);
        debug_playback_log_step("build_sink", step_started_at, profile_started_at);

        let safe_start_seconds = if decoded_duration > 0.0 {
            clamp_time(start_seconds).min(decoded_duration)
        } else {
            clamp_time(start_seconds)
        };
        let mut actual_start_seconds = safe_start_seconds;

        if safe_start_seconds > 0.0 {
            if supports_seek {
                step_started_at = Instant::now();
                sink.try_seek(Duration::from_secs_f64(safe_start_seconds))
                    .map_err(|error| format!("Failed to seek Rust playback stream: {error}"))?;
                debug_playback_log_step("seek_sink", step_started_at, profile_started_at);
            } else {
                actual_start_seconds = 0.0;
                playback_warning = append_warning(
                    playback_warning,
                    "DSD playback is active, but seeking within DSD tracks is not available yet. Playback started from the beginning.",
                );
            }
        }

        step_started_at = Instant::now();
        self.set_runtime_position(actual_start_seconds, autoplay);
        self.ended_track_id = None;
        self.error = playback_warning;
        self.signal_path = Some(signal_path);
        self.reset_audio_meter();
        debug_playback_log_step("apply_state", step_started_at, profile_started_at);

        if let Some(track) = &mut self.active_track {
            track.duration = decoded_duration.max(track.duration);
        }

        if autoplay {
            sink.play();
        }

        self.sink = Some(sink);
        debug_playback_log(
            "rebuild_sink_complete",
            format!(
                "totalMs={} fileBytes={} autoplay={} startSeconds={:.3} duration={:.3} sampleRate={} channels={}",
                elapsed_ms_u64(profile_started_at),
                file_size,
                autoplay,
                actual_start_seconds,
                decoded_duration,
                source_sample_rate,
                source_channels
            ),
        );

        Ok(())
    }

    fn set_runtime_position(&mut self, seconds: f64, playing: bool) {
        let safe_seconds = clamp_time(seconds);
        self.current_time = safe_seconds;
        self.playback_anchor_time = safe_seconds;
        self.playback_anchor_started_at = if playing { Some(Instant::now()) } else { None };
        self.status = if playing {
            PlaybackStatus::Playing
        } else if self.active_track.is_some() {
            PlaybackStatus::Paused
        } else {
            PlaybackStatus::Idle
        };
        self.audio_meter
            .set_playback_state(self.status == PlaybackStatus::Playing, self.volume);
    }

    fn track_has_completed(&self) -> bool {
        self.active_track
            .as_ref()
            .is_some_and(|track| track.duration > 0.0 && self.current_time >= track.duration)
    }

    fn release_output(&mut self) {
        self.sink = None;
        self.output_stream = None;
        self.active_output_device_id = None;
        self.active_output_device_name = None;
        self.signal_path = None;
        self.playback_anchor_started_at = None;
        self.stop_audio_meter();
    }

    fn reset_audio_meter(&self) {
        self.audio_meter.reset();
        self.audio_meter
            .set_playback_state(self.status == PlaybackStatus::Playing, self.volume);
    }

    fn stop_audio_meter(&self) {
        self.audio_meter.reset();
        self.audio_meter.set_playback_state(false, self.volume);
    }

    fn audio_levels(&self) -> Vec<f32> {
        self.audio_meter
            .snapshot(self.volume, self.status == PlaybackStatus::Playing)
    }

    pub(crate) fn audio_meter_handle(&self) -> AudioMeter {
        self.audio_meter.clone()
    }

    fn sync_system_media_session(&mut self) {
        let status = match self.status {
            PlaybackStatus::Playing => SystemPlaybackStatus::Playing,
            PlaybackStatus::Paused if self.active_track.is_some() => SystemPlaybackStatus::Paused,
            _ => SystemPlaybackStatus::Stopped,
        };

        let metadata = self.active_track.as_ref().map(|track| {
            let mut media = track.media.clone();
            media.duration = track.duration.max(media.duration);
            media
        });

        self.system_media
            .sync(status, self.current_time, metadata.as_ref());
    }
}

fn clamp_time(value: f64) -> f64 {
    if value.is_finite() && value >= 0.0 {
        value
    } else {
        0.0
    }
}

impl Drop for PlaybackManager {
    fn drop(&mut self) {
        let previous_track = self.active_track.take();
        self.release_output();
        cleanup_released_track(previous_track);
    }
}

fn prepare_playback_source(active_track: &LoadedTrack) -> Result<PreparedPlaybackSource, String> {
    if audio_formats::is_dsd_audio_path(&active_track.path) {
        let dsd_source = DsdPcmSource::open(&active_track.path)?;
        let dsd_label = dsd_source.dsd_sample_rate / 44_100;
        let decoded_duration = dsd_source
            .duration
            .map(|duration| duration.as_secs_f64())
            .unwrap_or(active_track.duration);

        return Ok(PreparedPlaybackSource {
            source: Box::new(dsd_source.source),
            source_sample_rate: dsd_source.pcm_sample_rate,
            source_channels: dsd_source.channels,
            decoded_duration,
            source_bit_depth: Some(1),
            source_sample_format: format!("dsd{dsd_label}-to-pcm-f32"),
            supports_seek: false,
            warning: None,
        });
    }

    let file = File::open(&active_track.path).map_err(|error| {
        format!(
            "Failed to open '{}' for playback: {error}",
            active_track.path.display()
        )
    })?;
    let decoder = Decoder::try_from(file).map_err(|error| {
        format!(
            "Failed to decode '{}' for playback: {error}",
            active_track.path.display()
        )
    })?;
    let source_sample_rate = decoder.sample_rate();
    let source_channels = decoder.channels();
    let decoded_duration = decoder
        .total_duration()
        .map(|duration| duration.as_secs_f64())
        .unwrap_or(active_track.duration);

    Ok(PreparedPlaybackSource {
        source: Box::new(decoder),
        source_sample_rate,
        source_channels,
        decoded_duration,
        source_bit_depth: active_track.bit_depth,
        source_sample_format: String::from("decoded-f32"),
        supports_seek: true,
        warning: None,
    })
}

fn cleanup_released_track(track: Option<LoadedTrack>) {
    let Some(track) = track else {
        return;
    };

    if track.delete_on_release {
        let _ = std::fs::remove_file(track.path);
    }
}

fn append_optional_warning(left: Option<String>, right: Option<String>) -> Option<String> {
    match (left, right) {
        (Some(left), Some(right)) => append_warning(Some(left), &right),
        (Some(left), None) => Some(left),
        (None, Some(right)) => Some(right),
        (None, None) => None,
    }
}

fn append_warning(existing: Option<String>, message: &str) -> Option<String> {
    let message = message.trim();

    if message.is_empty() {
        return existing;
    }

    Some(match existing {
        Some(existing) if !existing.trim().is_empty() => format!("{} {}", existing.trim(), message),
        _ => String::from(message),
    })
}

fn elapsed_ms_u64(started_at: Instant) -> u64 {
    let millis = started_at.elapsed().as_millis();
    millis.min(u128::from(u64::MAX)) as u64
}

fn debug_playback_log(event: &str, detail: impl AsRef<str>) {
    #[cfg(debug_assertions)]
    {
        eprintln!("[OFPlayer playback] {event} {}", detail.as_ref());
    }

    #[cfg(not(debug_assertions))]
    {
        let _ = event;
        let _ = detail;
    }
}

fn debug_playback_log_step(event: &str, step_started_at: Instant, profile_started_at: Instant) {
    debug_playback_log(
        event,
        format!(
            "stepMs={} totalMs={}",
            elapsed_ms_u64(step_started_at),
            elapsed_ms_u64(profile_started_at)
        ),
    );
}

fn clamp_volume(value: f64) -> f32 {
    value.clamp(0.0, 1.0) as f32
}

fn normalize_output_device_id(value: Option<String>) -> Option<String> {
    value.and_then(|value| {
        let trimmed = value.trim();

        if trimmed.is_empty() {
            None
        } else {
            Some(String::from(trimmed))
        }
    })
}

const OUTPUT_DEVICE_ID_SEPARATOR: &str = "::";

struct OutputHost {
    key: String,
    label: String,
    is_default: bool,
    host: cpal::Host,
}

struct ParsedOutputDevicePreference {
    backend: Option<String>,
    device_name: String,
}

fn output_device_name(device: &cpal::Device) -> Option<String> {
    device
        .name()
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
}

fn playback_output_format(output_stream: &OutputStream) -> PlaybackSignalFormat {
    let config = output_stream.config();
    let sample_format = config.sample_format();

    PlaybackSignalFormat {
        sample_rate: config.sample_rate(),
        channels: config.channel_count(),
        bit_depth: sample_format_bit_depth(sample_format),
        sample_format: Some(format_sample_format(sample_format)),
    }
}

fn build_signal_path(
    source: PlaybackSignalFormat,
    output: PlaybackSignalFormat,
    volume: f32,
) -> PlaybackSignalPath {
    let resampled = source.sample_rate != output.sample_rate;
    let channel_converted = source.channels != output.channels;
    let sample_format_converted = source
        .bit_depth
        .zip(output.bit_depth)
        .is_some_and(|(source_bit_depth, output_bit_depth)| source_bit_depth != output_bit_depth);
    let software_mixer = true;
    let software_volume = volume < 0.999;

    PlaybackSignalPath {
        source,
        output,
        resampled,
        channel_converted,
        sample_format_converted,
        software_mixer,
        software_volume,
        bit_perfect: false,
        integrity_status: if resampled || channel_converted || sample_format_converted {
            "converted"
        } else if software_mixer || software_volume {
            "not-bit-perfect"
        } else {
            "bit-perfect"
        },
    }
}

fn format_sample_format(sample_format: cpal::SampleFormat) -> String {
    match sample_format {
        cpal::SampleFormat::I8 => String::from("i8"),
        cpal::SampleFormat::I16 => String::from("i16"),
        cpal::SampleFormat::I24 => String::from("i24"),
        cpal::SampleFormat::I32 => String::from("i32"),
        cpal::SampleFormat::I64 => String::from("i64"),
        cpal::SampleFormat::U8 => String::from("u8"),
        cpal::SampleFormat::U16 => String::from("u16"),
        cpal::SampleFormat::U32 => String::from("u32"),
        cpal::SampleFormat::U64 => String::from("u64"),
        cpal::SampleFormat::F32 => String::from("f32"),
        cpal::SampleFormat::F64 => String::from("f64"),
        _ => String::from("unknown"),
    }
}

fn sample_format_bit_depth(sample_format: cpal::SampleFormat) -> Option<u32> {
    match sample_format {
        cpal::SampleFormat::I8 | cpal::SampleFormat::U8 => Some(8),
        cpal::SampleFormat::I16 | cpal::SampleFormat::U16 => Some(16),
        cpal::SampleFormat::I24 => Some(24),
        cpal::SampleFormat::I32 | cpal::SampleFormat::U32 | cpal::SampleFormat::F32 => Some(32),
        cpal::SampleFormat::I64 | cpal::SampleFormat::U64 | cpal::SampleFormat::F64 => Some(64),
        _ => None,
    }
}

fn list_output_devices() -> Result<Vec<PlaybackOutputDevice>, String> {
    let hosts = available_output_hosts();
    let mut entries = Vec::new();
    let mut errors = Vec::new();

    for output_host in hosts {
        let default_device_name = if output_host.is_default {
            output_host
                .host
                .default_output_device()
                .and_then(|device| output_device_name(&device))
        } else {
            None
        };
        let devices = match output_host.host.output_devices() {
            Ok(devices) => devices,
            Err(error) => {
                errors.push(format!(
                    "Failed to enumerate {} output devices: {error}",
                    output_host.label
                ));
                continue;
            }
        };

        for device in devices {
            let Some(device_name) = output_device_name(&device) else {
                continue;
            };
            let device_id = encode_output_device_id(&output_host.key, &device_name);

            entries.push(PlaybackOutputDevice {
                id: device_id,
                name: device_name.clone(),
                backend: output_host.key.clone(),
                backend_label: output_host.label.clone(),
                is_default: output_host.is_default
                    && default_device_name.as_deref() == Some(device_name.as_str()),
            });
        }
    }

    if entries.is_empty() && !errors.is_empty() {
        return Err(errors.join(" "));
    }

    entries.sort_by(|left, right| {
        right
            .is_default
            .cmp(&left.is_default)
            .then_with(|| {
                output_backend_sort_key(&left.backend).cmp(&output_backend_sort_key(&right.backend))
            })
            .then_with(|| left.name.to_lowercase().cmp(&right.name.to_lowercase()))
    });

    Ok(entries)
}

fn build_output_devices_snapshot(
    preferred_output_device_id: Option<String>,
    active_output_device_id: Option<String>,
    active_output_device_name: Option<String>,
) -> Result<PlaybackOutputDevicesSnapshot, String> {
    let devices = list_output_devices()?;
    let preferred_device = preferred_output_device_id
        .as_ref()
        .and_then(|preferred_device_id| find_device_for_preference(&devices, preferred_device_id));
    let preferred_device_available =
        preferred_output_device_id.is_none() || preferred_device.is_some();
    let normalized_preferred_device_id = preferred_device
        .map(|device| device.id.clone())
        .or_else(|| preferred_output_device_id.clone());
    let resolved_active_device_id = active_output_device_id
        .as_ref()
        .and_then(|active_device_id| {
            find_device_for_preference(&devices, active_device_id).map(|device| device.id.clone())
        })
        .or_else(|| {
            normalized_preferred_device_id
                .as_ref()
                .and_then(|preferred_device_id| {
                    find_device_for_preference(&devices, preferred_device_id)
                        .map(|device| device.id.clone())
                })
        })
        .or_else(|| {
            devices
                .iter()
                .find(|device| device.is_default)
                .map(|device| device.id.clone())
        });
    let active_device_name = resolved_active_device_id
        .as_ref()
        .and_then(|active_device_id| {
            devices
                .iter()
                .find(|device| device.id == *active_device_id)
                .map(|device| device.name.clone())
        });
    let active_device_name = active_device_name.or(active_output_device_name);

    Ok(PlaybackOutputDevicesSnapshot {
        devices,
        preferred_device_id: normalized_preferred_device_id,
        active_device_id: resolved_active_device_id,
        active_device_name,
        prefers_system_default: preferred_output_device_id.is_none(),
        preferred_device_available,
    })
}

fn open_stream_for_device(device: cpal::Device) -> Result<OutputStream, String> {
    OutputStreamBuilder::from_device(device)
        .map_err(|error| format!("Failed to prepare the selected audio output device: {error}"))?
        .open_stream_or_fallback()
        .map_err(|error| format!("Failed to open the selected audio output device: {error}"))
}

fn open_first_available_output_stream(
    output_host: &OutputHost,
    excluded_device_name: Option<&str>,
) -> Result<ResolvedOutputStream, String> {
    let devices = output_host
        .host
        .output_devices()
        .map_err(|error| format!("Failed to enumerate audio output devices: {error}"))?;
    let mut last_error: Option<String> = None;

    for device in devices {
        let device_name = output_device_name(&device);

        if excluded_device_name.is_some() && device_name.as_deref() == excluded_device_name {
            continue;
        }

        match open_stream_for_device(device) {
            Ok(stream) => {
                return Ok(ResolvedOutputStream {
                    stream,
                    active_device_id: device_name
                        .as_ref()
                        .map(|name| encode_output_device_id(&output_host.key, name)),
                    active_device_name: device_name,
                    warning: None,
                });
            }
            Err(error) => {
                last_error = Some(error);
            }
        }
    }

    Err(last_error
        .unwrap_or_else(|| String::from("No audio output device is currently available.")))
}

fn open_default_output_stream() -> Result<ResolvedOutputStream, String> {
    let output_host = output_host_from_host(cpal::default_host(), true);
    let default_device = output_host.host.default_output_device();

    if let Some(device) = default_device {
        let default_device_name = output_device_name(&device);

        match open_stream_for_device(device) {
            Ok(stream) => {
                return Ok(ResolvedOutputStream {
                    stream,
                    active_device_id: default_device_name
                        .as_ref()
                        .map(|name| encode_output_device_id(&output_host.key, name)),
                    active_device_name: default_device_name,
                    warning: None,
                });
            }
            Err(default_error) => {
                let mut fallback_stream = open_first_available_output_stream(
                    &output_host,
                    default_device_name.as_deref(),
                )?;
                let fallback_name = fallback_stream
                    .active_device_name
                    .as_deref()
                    .unwrap_or("another output");
                fallback_stream.warning = Some(format!(
                    "Failed to open the system default output device: {default_error}. Switched playback to {fallback_name} instead.",
                ));
                return Ok(fallback_stream);
            }
        }
    }

    open_first_available_output_stream(&output_host, None)
}

fn resolve_output_stream(
    preferred_output_device_id: Option<&str>,
) -> Result<ResolvedOutputStream, String> {
    if let Some(preferred_output_device_id) = preferred_output_device_id
        .map(str::trim)
        .filter(|device_id| !device_id.is_empty())
    {
        match find_output_device_by_preference(preferred_output_device_id)? {
            Some((device, output_host, device_name)) => match open_stream_for_device(device) {
                Ok(stream) => {
                    return Ok(ResolvedOutputStream {
                        stream,
                        active_device_id: Some(encode_output_device_id(
                            &output_host.key,
                            &device_name,
                        )),
                        active_device_name: Some(device_name),
                        warning: None,
                    });
                }
                Err(preferred_error) => {
                    let mut fallback_stream = open_default_output_stream()?;
                    let fallback_name = fallback_stream
                        .active_device_name
                        .as_deref()
                        .unwrap_or("the system default output");
                    fallback_stream.warning = Some(format!(
                        "Failed to open preferred output device \"{preferred_output_device_id}\": {preferred_error}. Using {fallback_name} instead.",
                    ));
                    return Ok(fallback_stream);
                }
            },
            None => {
                let mut fallback_stream = open_default_output_stream()?;
                let fallback_name = fallback_stream
                    .active_device_name
                    .as_deref()
                    .unwrap_or("the system default output");
                fallback_stream.warning = Some(format!(
                    "Preferred output device \"{preferred_output_device_id}\" is unavailable. Using {fallback_name} instead.",
                ));
                return Ok(fallback_stream);
            }
        }
    }

    open_default_output_stream()
}

fn available_output_hosts() -> Vec<OutputHost> {
    let default_host = cpal::default_host();
    let default_host_id = default_host.id();
    let mut hosts = cpal::available_hosts()
        .into_iter()
        .filter_map(|host_id| {
            cpal::host_from_id(host_id).ok().map(|host| {
                let is_default = host_id == default_host_id;
                output_host_from_host(host, is_default)
            })
        })
        .collect::<Vec<_>>();

    if hosts.is_empty() {
        hosts.push(output_host_from_host(default_host, true));
    }

    hosts.sort_by(|left, right| {
        right.is_default.cmp(&left.is_default).then_with(|| {
            output_backend_sort_key(&left.key).cmp(&output_backend_sort_key(&right.key))
        })
    });
    hosts
}

fn output_host_from_host(host: cpal::Host, is_default: bool) -> OutputHost {
    let host_id = host.id();
    let label = host_id.name().to_string();
    let key = output_backend_key(&label);

    OutputHost {
        key,
        label,
        is_default,
        host,
    }
}

fn output_backend_key(label: &str) -> String {
    label.trim().to_ascii_lowercase()
}

fn output_backend_sort_key(key: &str) -> u8 {
    match key {
        "wasapi" => 0,
        "asio" => 1,
        _ => 2,
    }
}

fn encode_output_device_id(backend: &str, device_name: &str) -> String {
    format!("{backend}{OUTPUT_DEVICE_ID_SEPARATOR}{device_name}")
}

fn parse_output_device_preference(value: &str) -> ParsedOutputDevicePreference {
    let trimmed = value.trim();

    if let Some((backend, device_name)) = trimmed.split_once(OUTPUT_DEVICE_ID_SEPARATOR) {
        let backend = backend.trim();
        let device_name = device_name.trim();

        if !backend.is_empty() && !device_name.is_empty() {
            return ParsedOutputDevicePreference {
                backend: Some(output_backend_key(backend)),
                device_name: device_name.to_string(),
            };
        }
    }

    ParsedOutputDevicePreference {
        backend: None,
        device_name: trimmed.to_string(),
    }
}

fn find_device_for_preference<'a>(
    devices: &'a [PlaybackOutputDevice],
    preferred_device_id: &str,
) -> Option<&'a PlaybackOutputDevice> {
    let preference = parse_output_device_preference(preferred_device_id);

    devices.iter().find(|device| {
        if device.id == preferred_device_id {
            return true;
        }

        if let Some(backend) = &preference.backend {
            device.backend == *backend && device.name == preference.device_name
        } else {
            device.backend != "asio" && device.name == preference.device_name
        }
    })
}

fn find_output_device_by_preference(
    preferred_device_id: &str,
) -> Result<Option<(cpal::Device, OutputHost, String)>, String> {
    let preference = parse_output_device_preference(preferred_device_id);

    for output_host in available_output_hosts() {
        if let Some(backend) = &preference.backend {
            if output_host.key != *backend {
                continue;
            }
        } else if !output_host.is_default {
            continue;
        }

        let devices = output_host.host.output_devices().map_err(|error| {
            format!(
                "Failed to enumerate {} output devices: {error}",
                output_host.label
            )
        })?;

        for device in devices {
            let Some(device_name) = output_device_name(&device) else {
                continue;
            };

            if device_name == preference.device_name {
                return Ok(Some((device, output_host, device_name)));
            }
        }
    }

    Ok(None)
}

fn build_system_media_metadata(request: &LoadTrackRequest) -> SystemMediaMetadata {
    let media = request.media.clone().unwrap_or_default();
    let fallback_title = request
        .path
        .rsplit(['\\', '/'])
        .next()
        .unwrap_or("Untitled")
        .trim()
        .to_string();

    SystemMediaMetadata {
        track_id: request.track_id.clone(),
        title: media
            .title
            .map(|value| value.trim().to_string())
            .filter(|value| !value.is_empty())
            .unwrap_or(fallback_title),
        artist: media
            .artist
            .map(|value| value.trim().to_string())
            .unwrap_or_default(),
        album: media
            .album
            .map(|value| value.trim().to_string())
            .unwrap_or_default(),
        cover_url: media
            .cover_url
            .map(|value| value.trim().to_string())
            .unwrap_or_default(),
        duration: clamp_time(request.duration_hint.unwrap_or(0.0)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct SeekableTestSource {
        position: usize,
        sample_rate: u32,
        channels: u16,
    }

    impl SeekableTestSource {
        fn new() -> Self {
            Self {
                position: 0,
                sample_rate: 1_000,
                channels: 2,
            }
        }
    }

    impl Iterator for SeekableTestSource {
        type Item = f32;

        fn next(&mut self) -> Option<Self::Item> {
            self.position += 1;
            Some(0.25)
        }
    }

    impl Source for SeekableTestSource {
        fn current_span_len(&self) -> Option<usize> {
            None
        }

        fn channels(&self) -> u16 {
            self.channels
        }

        fn sample_rate(&self) -> u32 {
            self.sample_rate
        }

        fn total_duration(&self) -> Option<Duration> {
            Some(Duration::from_secs(10))
        }

        fn try_seek(&mut self, pos: Duration) -> Result<(), SeekError> {
            self.position = pos.as_millis() as usize;
            Ok(())
        }
    }

    #[test]
    fn frequency_bin_range_skips_bands_above_nyquist() {
        assert_eq!(
            frequency_bin_range(16_000, AUDIO_METER_FFT_SIZE, 8_000.0, 12_000.0),
            None
        );
    }

    #[test]
    fn frequency_bin_range_keeps_valid_bands_ordered() {
        let (start, end) = frequency_bin_range(48_000, AUDIO_METER_FFT_SIZE, 2_000.0, 4_000.0)
            .expect("2k-4k should fit in a 48kHz FFT");

        assert!(start > 0);
        assert!(end > start);
        assert!(end <= AUDIO_METER_FFT_SIZE / 2);
    }

    #[test]
    fn audio_meter_smooths_attacks_before_snapshot() {
        let meter = AudioMeter::default();
        meter.push_levels([1.0; AUDIO_METER_LEVEL_COUNT]);

        let snapshot = meter.snapshot(1.0, true);

        assert_eq!(snapshot.len(), AUDIO_METER_LEVEL_COUNT);
        assert!(snapshot.iter().all(|level| *level > 0.5 && *level < 1.0));
    }

    #[test]
    fn spectrum_analyzer_raises_the_matching_frequency_band() {
        let sample_rate = 48_000;
        let mut analyzer = SpectrumAnalyzer::new(sample_rate);
        let mut latest_levels = None;

        for sample_index in 0..(AUDIO_METER_FFT_SIZE + AUDIO_METER_HOP_SIZE) {
            let phase = std::f32::consts::TAU * 100.0 * sample_index as f32 / sample_rate as f32;
            latest_levels = analyzer
                .push_mono_sample(phase.sin() * 0.75)
                .or(latest_levels);
        }

        let levels = latest_levels.expect("analyzer should emit levels after one FFT window");

        assert!(levels[0] > 0.25);
        assert!(levels[0] > levels[3]);
    }

    #[test]
    fn metered_source_forwards_seek_to_inner_source() {
        let meter = AudioMeter::default();
        let mut source = MeteredSource::new(SeekableTestSource::new(), meter);

        source.next();
        assert_eq!(source.channel_index, 1);

        source.try_seek(Duration::from_millis(2_500)).unwrap();

        assert_eq!(source.inner.position, 2_500);
        assert_eq!(source.channel_index, 0);
        assert_eq!(source.frame_sum, 0.0);
    }

    #[test]
    fn reset_audio_meter_preserves_playing_state() {
        let mut playback = PlaybackManager::new();
        playback.status = PlaybackStatus::Playing;

        playback.reset_audio_meter();

        assert!(playback.audio_meter.inner.playing.load(Ordering::Acquire));

        playback.stop_audio_meter();

        assert!(!playback.audio_meter.inner.playing.load(Ordering::Acquire));
    }
}
