use dsd_reader::{DsdIter, DsdReader};
use rodio::{source::SeekError, Source};
use std::{
    collections::VecDeque,
    io,
    path::{Path, PathBuf},
    time::Duration,
};

const DSD64_SAMPLE_RATE: u32 = 2_822_400;
const DSD_TO_PCM_DECIMATION_BITS: u32 = 64;

pub(crate) struct DsdPcmSource {
    path: PathBuf,
    iter: DsdIter,
    channels: u16,
    pcm_sample_rate: u32,
    duration: Option<Duration>,
    pending_samples: VecDeque<f32>,
    channel_sums: Vec<i32>,
    channel_bit_counts: Vec<u32>,
    exhausted: bool,
}

pub(crate) struct DsdPcmSourceInfo {
    pub source: DsdPcmSource,
    pub dsd_sample_rate: u32,
    pub pcm_sample_rate: u32,
    pub channels: u16,
    pub duration: Option<Duration>,
}

impl DsdPcmSource {
    pub(crate) fn open(path: &Path) -> Result<DsdPcmSourceInfo, String> {
        let path = path.to_path_buf();
        let reader = open_reader(&path)?;
        let channels = normalize_channel_count(reader.channels_num())?;
        let dsd_sample_rate = normalize_dsd_sample_rate(reader.dsd_rate())?;
        let pcm_sample_rate = (dsd_sample_rate / DSD_TO_PCM_DECIMATION_BITS).max(1);
        let duration = duration_from_dsd_bytes(reader.audio_length(), channels, dsd_sample_rate);
        let iter = reader
            .interl_iter(true, None)
            .map_err(|error| format!("Failed to read DSD audio frames: {error}"))?;

        let source = Self {
            path,
            iter,
            channels,
            pcm_sample_rate,
            duration,
            pending_samples: VecDeque::new(),
            channel_sums: vec![0; usize::from(channels)],
            channel_bit_counts: vec![0; usize::from(channels)],
            exhausted: false,
        };

        Ok(DsdPcmSourceInfo {
            source,
            dsd_sample_rate,
            pcm_sample_rate,
            channels,
            duration,
        })
    }

    fn reset_to_start(&mut self) -> Result<(), String> {
        let reader = open_reader(&self.path)?;
        self.iter = reader
            .interl_iter(true, None)
            .map_err(|error| format!("Failed to reset DSD audio frames: {error}"))?;
        self.pending_samples.clear();
        self.channel_sums.fill(0);
        self.channel_bit_counts.fill(0);
        self.exhausted = false;
        Ok(())
    }

    fn fill_pending_samples(&mut self) {
        if self.exhausted {
            return;
        }

        while self.pending_samples.is_empty() {
            let Some((_read_size, buffers)) = self.iter.next() else {
                self.exhausted = true;
                return;
            };
            let Some(interleaved_bytes) = buffers.first() else {
                continue;
            };
            let channel_count = usize::from(self.channels);

            for frame_bytes in interleaved_bytes.chunks_exact(channel_count) {
                for (channel_index, byte) in frame_bytes.iter().copied().enumerate() {
                    self.push_dsd_byte(channel_index, byte);
                }
            }
        }
    }

    fn push_dsd_byte(&mut self, channel_index: usize, byte: u8) {
        for bit_index in 0..8 {
            let bit = (byte >> bit_index) & 1;
            self.channel_sums[channel_index] += if bit == 0 { -1 } else { 1 };
            self.channel_bit_counts[channel_index] += 1;

            if self.channel_bit_counts[channel_index] >= DSD_TO_PCM_DECIMATION_BITS {
                let sample =
                    self.channel_sums[channel_index] as f32 / DSD_TO_PCM_DECIMATION_BITS as f32;
                self.pending_samples.push_back(sample.clamp(-1.0, 1.0));
                self.channel_sums[channel_index] = 0;
                self.channel_bit_counts[channel_index] = 0;
            }
        }
    }
}

impl Iterator for DsdPcmSource {
    type Item = f32;

    fn next(&mut self) -> Option<Self::Item> {
        if self.pending_samples.is_empty() {
            self.fill_pending_samples();
        }

        self.pending_samples.pop_front()
    }
}

impl Source for DsdPcmSource {
    fn current_span_len(&self) -> Option<usize> {
        None
    }

    fn channels(&self) -> u16 {
        self.channels
    }

    fn sample_rate(&self) -> u32 {
        self.pcm_sample_rate
    }

    fn total_duration(&self) -> Option<Duration> {
        self.duration
    }

    fn try_seek(&mut self, pos: Duration) -> Result<(), SeekError> {
        if pos.is_zero() {
            return self
                .reset_to_start()
                .map_err(|error| SeekError::Other(Box::new(io::Error::other(error))));
        }

        Err(SeekError::NotSupported {
            underlying_source: std::any::type_name::<Self>(),
        })
    }
}

pub(crate) fn dsd_duration_seconds(
    audio_length_bytes: u64,
    channels: u16,
    sample_rate: u32,
) -> f64 {
    if channels == 0 || sample_rate == 0 {
        return 0.0;
    }

    audio_length_bytes as f64 * 8.0 / f64::from(channels) / f64::from(sample_rate)
}

pub(crate) fn dsd_sample_rate_from_multiplier(multiplier: i32) -> Result<u32, String> {
    normalize_dsd_sample_rate(multiplier)
}

fn open_reader(path: &Path) -> Result<DsdReader, String> {
    DsdReader::from_container(path.to_path_buf())
        .map_err(|error| format!("Failed to open DSD container '{}': {error}", path.display()))
}

fn normalize_channel_count(channels: usize) -> Result<u16, String> {
    let channels =
        u16::try_from(channels).map_err(|_| String::from("DSD channel count is too large."))?;

    if channels == 0 {
        return Err(String::from("DSD audio does not report any channels."));
    }

    Ok(channels)
}

fn normalize_dsd_sample_rate(multiplier: i32) -> Result<u32, String> {
    let multiplier = u32::try_from(multiplier)
        .map_err(|_| String::from("DSD sample-rate multiplier is invalid."))?;

    DSD64_SAMPLE_RATE
        .checked_mul(multiplier)
        .filter(|value| *value > 0)
        .ok_or_else(|| String::from("DSD sample rate is invalid."))
}

fn duration_from_dsd_bytes(
    audio_length_bytes: u64,
    channels: u16,
    sample_rate: u32,
) -> Option<Duration> {
    let seconds = dsd_duration_seconds(audio_length_bytes, channels, sample_rate);

    if seconds.is_finite() && seconds > 0.0 {
        Some(Duration::from_secs_f64(seconds))
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dsd_duration_uses_one_bit_samples() {
        assert_eq!(dsd_duration_seconds(705_600, 2, DSD64_SAMPLE_RATE), 1.0);
    }

    #[test]
    fn dsd_sample_rate_scales_from_dsd64() {
        assert_eq!(dsd_sample_rate_from_multiplier(4).unwrap(), 11_289_600);
    }
}
