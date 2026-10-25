use super::*;

fn player_with_current_state() -> PlaybackManager {
    let mut player = PlaybackManager::new();
    player.active_track = Some(LoadedTrack {
        id: "current".into(),
        path: "existing.wav".into(),
        duration: 60.0,
        sample_rate_hint: None,
        bit_depth: None,
        delete_on_release: false,
        media: SystemMediaMetadata::default(),
    });
    player.current_time = 12.5;
    player.status = PlaybackStatus::Playing;
    player.volume = 0.7;
    player
}
fn request(path: PathBuf) -> LoadTrackRequest {
    LoadTrackRequest {
        track_id: "candidate".into(),
        path: path.to_string_lossy().into(),
        autoplay: false,
        start_time: None,
        duration_hint: None,
        sample_rate: None,
        bit_depth: None,
        volume: Some(0.1),
        delete_on_release: Some(false),
        media: None,
    }
}
fn assert_unchanged(player: &PlaybackManager) {
    assert_eq!(player.active_track.as_ref().unwrap().id, "current");
    assert_eq!(player.status, PlaybackStatus::Playing);
    assert_eq!(player.current_time, 12.5);
    assert_eq!(player.volume, 0.7);
    assert!(player.error.is_none());
}
#[test]
fn missing_file_does_not_mutate_existing_player() {
    let mut player = player_with_current_state();
    let missing =
        std::env::temp_dir().join(format!("ofplayer-missing-{}.wav", uuid::Uuid::new_v4()));
    assert!(player.load_track(request(missing)).is_err());
    assert_unchanged(&player);
}
#[test]
fn corrupt_decoder_input_does_not_mutate_existing_player_or_open_output() {
    let temp = std::env::temp_dir().canonicalize().unwrap();
    let root = temp.join(format!("ofplayer-decoder-test-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir(&root).unwrap();
    let file = root.join("corrupt.mp3");
    std::fs::write(&file, b"not an encoded audio file").unwrap();
    let mut player = player_with_current_state();
    let result = player.load_track(request(file));
    assert_eq!(root.canonicalize().unwrap().parent(), Some(temp.as_path()));
    std::fs::remove_dir_all(&root).unwrap();
    assert!(result.is_err());
    assert_unchanged(&player);
    assert!(player.output_stream.is_none());
}
