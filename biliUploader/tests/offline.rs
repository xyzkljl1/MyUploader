use bili_uploader::backend::snapshot_from_value;
use bili_uploader::model::{Chapter, Copyright, PartSnapshot, RemoteSnapshot, Request, Visibility};
use bili_uploader::plan::{envelope, select_part};
use bili_uploader::validation::{validate_and_normalize, validate_bvid};
use serde_json::json;
use std::fs;
use uuid::Uuid;

fn remote(parts: Vec<PartSnapshot>) -> RemoteSnapshot {
    RemoteSnapshot {
        bvid: "BV1ab411c7mD".into(),
        aid: 123,
        title: "Title".into(),
        description: "Description".into(),
        category_id: 17,
        tags: "one,two".into(),
        cover: "https://example.invalid/cover.jpg".into(),
        copyright: 1,
        source: "".into(),
        visibility: Visibility::Public,
        parts,
    }
}

fn part(index: usize, cid: u64) -> PartSnapshot {
    PartSnapshot {
        index,
        cid,
        title: format!("P{}", index + 1),
        filename: format!("file-{cid}"),
    }
}

#[test]
fn single_part_is_selected_automatically() {
    let selected = select_part(&remote(vec![part(0, 42)]), None).unwrap();
    assert_eq!(selected.cid, 42);
    assert_eq!(selected.index, 0);
}

#[test]
fn multiple_parts_require_a_cid() {
    let error = select_part(&remote(vec![part(0, 42), part(1, 43)]), None).unwrap_err();
    assert_eq!(error.exit_code, 2);
}

#[test]
fn selected_cid_must_belong_to_bvid() {
    let error = select_part(&remote(vec![part(0, 42)]), Some(99)).unwrap_err();
    assert_eq!(error.exit_code, 2);
}

#[test]
fn native_chapters_stop_before_file_or_credentials() {
    let request = Request::SetChapters {
        request_id: Uuid::nil(),
        bvid: "BV1ab411c7mD".into(),
        cid: None,
        chapters: vec![Chapter {
            start_seconds: 0,
            title: "Start".into(),
        }],
    };
    let error = validate_and_normalize(request, std::path::Path::new("missing-base")).unwrap_err();
    assert_eq!(error.exit_code, 2);
    assert!(format!("{:?}", error.code).contains("UnsupportedChapterWrite"));
}

#[test]
fn create_with_chapters_does_not_touch_video_path() {
    let request = Request::Create {
        request_id: Uuid::nil(),
        video_path: "definitely-missing.mp4".into(),
        title: "Title".into(),
        description: None,
        tags: vec!["tag".into()],
        category_id: 17,
        copyright: Copyright::Original,
        source: None,
        cover_path: None,
        part_title: None,
        visibility: None,
        chapters: Some(vec![Chapter {
            start_seconds: 0,
            title: "Start".into(),
        }]),
    };
    let error = validate_and_normalize(request, std::path::Path::new(".")).unwrap_err();
    assert!(format!("{:?}", error.code).contains("UnsupportedChapterWrite"));
}

#[test]
fn rejects_unknown_request_fields() {
    let parsed = serde_json::from_value::<Request>(json!({
        "operation": "update-info",
        "requestId": "00000000-0000-0000-0000-000000000000",
        "bvid": "BV1ab411c7mD",
        "title": "New title",
        "unexpected": true
    }));
    assert!(parsed.is_err());
}

#[test]
fn parses_remote_ids_without_confusing_bvid_aid_and_cid() {
    let value = json!({
        "archive": {
            "bvid": "BV1ab411c7mD", "aid": 123, "title": "Title", "desc": "D",
            "tid": 17, "tag": "one,two", "cover": "cover", "copyright": 1, "source": "",
            "is_only_self": 1
        },
        "videos": [
            { "cid": 456, "title": "P1", "filename": "remote-file", "desc": "" }
        ]
    });
    let snapshot = snapshot_from_value(&value, Some("BV1ab411c7mD")).unwrap();
    assert_eq!(snapshot.aid, 123);
    assert_eq!(snapshot.parts[0].cid, 456);
    assert_eq!(snapshot.visibility, Visibility::SelfOnly);
}

#[test]
fn plan_digest_changes_with_plan_data() {
    let request = Request::UpdateInfo {
        request_id: Uuid::nil(),
        bvid: "BV1ab411c7mD".into(),
        title: Some("New".into()),
        description: None,
        tags: None,
        category_id: None,
        copyright: None,
        source: None,
        cover_path: None,
        visibility: None,
    };
    let first = bili_uploader::model::Plan {
        request: request.clone(),
        created_at_epoch_seconds: 1,
        expires_at_epoch_seconds: 2,
        media: None,
        cover: None,
        remote: None,
        target_part: None,
        changes: json!({"title":"New"}),
    };
    let mut second = first.clone();
    second.expires_at_epoch_seconds = 3;
    assert_ne!(
        envelope(first).unwrap().plan_sha256,
        envelope(second).unwrap().plan_sha256
    );
}

#[test]
fn media_is_hashed_and_path_is_canonicalized() {
    let root = std::env::temp_dir().join(format!("bili-uploader-test-{}", Uuid::new_v4()));
    fs::create_dir_all(&root).unwrap();
    fs::write(root.join("video.mp4"), b"synthetic video bytes").unwrap();
    let request = Request::Create {
        request_id: Uuid::new_v4(),
        video_path: "video.mp4".into(),
        title: "Title".into(),
        description: None,
        tags: vec!["tag".into()],
        category_id: 17,
        copyright: Copyright::Original,
        source: None,
        cover_path: None,
        part_title: None,
        visibility: Some(Visibility::SelfOnly),
        chapters: None,
    };
    let normalized = validate_and_normalize(request, &root).unwrap();
    if let Request::Create { video_path, .. } = normalized {
        assert!(std::path::Path::new(&video_path).is_absolute());
    } else {
        panic!("wrong operation");
    }
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn validates_bvid_shape() {
    assert!(validate_bvid("BV1ab411c7mD").is_ok());
    assert!(validate_bvid("av123").is_err());
}

#[test]
fn parses_visibility_for_create_and_update() {
    let create = serde_json::from_value::<Request>(json!({
        "operation": "create",
        "requestId": "00000000-0000-0000-0000-000000000001",
        "videoPath": "video.mp4",
        "title": "Title",
        "tags": ["tag"],
        "categoryId": 17,
        "copyright": "original",
        "visibility": "self-only"
    }))
    .unwrap();
    assert!(matches!(
        create,
        Request::Create {
            visibility: Some(Visibility::SelfOnly),
            ..
        }
    ));

    let update = serde_json::from_value::<Request>(json!({
        "operation": "update-info",
        "requestId": "00000000-0000-0000-0000-000000000002",
        "bvid": "BV1ab411c7mD",
        "visibility": "public"
    }))
    .unwrap();
    assert!(matches!(
        update,
        Request::UpdateInfo {
            visibility: Some(Visibility::Public),
            ..
        }
    ));
}
