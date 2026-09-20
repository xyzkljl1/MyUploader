use crate::error::{AppError, ErrorCode};
use crate::model::{PartSnapshot, RemoteSnapshot, Visibility};
use biliup::client::StatelessClient;
use biliup::uploader::VideoFile;
use biliup::uploader::bilibili::{BiliBili, Studio, Vid, Video};
use biliup::uploader::credential;
use biliup::uploader::line;
use futures::StreamExt;
use serde_json::Value;
use std::path::Path;

const MAX_CREDENTIAL_BYTES: u64 = 1024 * 1024;

pub fn open_credentials(path: &Path) -> Result<BiliBili, AppError> {
    let metadata = std::fs::symlink_metadata(path).map_err(|_| {
        AppError::environment(ErrorCode::CredentialError, "credential file does not exist")
    })?;
    if !metadata.is_file()
        || metadata.file_type().is_symlink()
        || metadata.len() == 0
        || metadata.len() > MAX_CREDENTIAL_BYTES
    {
        return Err(AppError::environment(
            ErrorCode::CredentialError,
            "credential file must be a non-empty regular file no larger than 1 MiB",
        ));
    }
    credential::bilibili_from_cookies(path, None).map_err(|_| {
        AppError::environment(
            ErrorCode::CredentialError,
            "credential file could not be loaded",
        )
    })
}

pub fn ensure_credential_target_safe(path: &Path) -> Result<(), AppError> {
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_symlink() || !metadata.is_file() => {
            Err(AppError::environment(
                ErrorCode::CredentialError,
                "configuration target must be a regular file and not a symbolic link",
            ))
        }
        Ok(_) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(_) => Err(AppError::environment(
            ErrorCode::CredentialError,
            "configuration target could not be inspected",
        )),
    }
}

pub async fn fetch_snapshot(bili: &BiliBili, bvid: &str) -> Result<RemoteSnapshot, AppError> {
    let raw = bili
        .video_data(&Vid::Bvid(bvid.to_string()), None)
        .await
        .map_err(|_| {
            AppError::environment(ErrorCode::NetworkError, "could not read the target稿件")
        })?;
    snapshot_from_value(&raw, Some(bvid))
}

pub fn snapshot_from_value(
    raw: &Value,
    expected_bvid: Option<&str>,
) -> Result<RemoteSnapshot, AppError> {
    let archive = raw
        .get("archive")
        .and_then(Value::as_object)
        .ok_or_else(|| {
            AppError::environment(
                ErrorCode::NetworkError,
                "稿件 response did not contain archive data",
            )
        })?;
    let string = |name: &str| {
        archive
            .get(name)
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_string()
    };
    let bvid = string("bvid");
    if bvid.is_empty() || expected_bvid.is_some_and(|expected| expected != bvid) {
        return Err(AppError::input(
            ErrorCode::TargetMismatch,
            "returned BVID does not match the requested target",
        ));
    }
    let aid = archive.get("aid").and_then(Value::as_u64).ok_or_else(|| {
        AppError::environment(
            ErrorCode::NetworkError,
            "稿件 response did not contain a valid AID",
        )
    })?;
    let category_id = archive
        .get("tid")
        .and_then(Value::as_u64)
        .and_then(|v| u16::try_from(v).ok())
        .ok_or_else(|| {
            AppError::environment(
                ErrorCode::NetworkError,
                "稿件 response did not contain a valid category",
            )
        })?;
    let copyright = archive
        .get("copyright")
        .and_then(Value::as_u64)
        .and_then(|v| u8::try_from(v).ok())
        .unwrap_or(0);
    let visibility = match archive.get("is_only_self").and_then(Value::as_u64) {
        None | Some(0) => Visibility::Public,
        Some(1) => Visibility::SelfOnly,
        Some(_) => {
            return Err(AppError::environment(
                ErrorCode::NetworkError,
                "稿件 response contained an unknown visibility value",
            ));
        }
    };
    let videos = raw.get("videos").and_then(Value::as_array).ok_or_else(|| {
        AppError::environment(
            ErrorCode::NetworkError,
            "稿件 response did not contain parts",
        )
    })?;
    if videos.is_empty() {
        return Err(AppError::input(
            ErrorCode::TargetMismatch,
            "target稿件 has no video parts",
        ));
    }
    let mut parts = Vec::with_capacity(videos.len());
    for (index, video) in videos.iter().enumerate() {
        let cid = video.get("cid").and_then(Value::as_u64).ok_or_else(|| {
            AppError::environment(ErrorCode::NetworkError, "a video part has no valid CID")
        })?;
        parts.push(PartSnapshot {
            index,
            cid,
            title: video
                .get("title")
                .and_then(Value::as_str)
                .unwrap_or("")
                .to_string(),
            filename: video
                .get("filename")
                .and_then(Value::as_str)
                .unwrap_or("")
                .to_string(),
        });
    }
    Ok(RemoteSnapshot {
        bvid,
        aid,
        title: string("title"),
        description: string("desc"),
        category_id,
        tags: string("tag"),
        cover: archive
            .get("cover")
            .or_else(|| archive.get("pic"))
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_string(),
        copyright,
        source: string("source"),
        visibility,
        parts,
    })
}

pub async fn fetch_editable(
    bili: &BiliBili,
    bvid: &str,
) -> Result<(RemoteSnapshot, Studio), AppError> {
    let mut raw = bili
        .video_data(&Vid::Bvid(bvid.to_string()), None)
        .await
        .map_err(|_| {
            AppError::environment(ErrorCode::NetworkError, "could not load editable稿件 data")
        })?;
    let snapshot = snapshot_from_value(&raw, Some(bvid))?;
    let mut archive = raw
        .get_mut("archive")
        .map(Value::take)
        .ok_or_else(|| AppError::environment(ErrorCode::NetworkError, "missing archive data"))?;
    if let Some(object) = archive.as_object_mut() {
        object.remove("limited_free");
    }
    let mut studio: Studio = serde_json::from_value(archive).map_err(|_| {
        AppError::environment(
            ErrorCode::NetworkError,
            "稿件 archive data is not compatible with pinned biliup",
        )
    })?;
    studio.videos = serde_json::from_value(
        raw.get_mut("videos")
            .map(Value::take)
            .ok_or_else(|| AppError::environment(ErrorCode::NetworkError, "missing part data"))?,
    )
    .map_err(|_| {
        AppError::environment(
            ErrorCode::NetworkError,
            "稿件 part data is not compatible with pinned biliup",
        )
    })?;
    Ok((snapshot, studio))
}

pub async fn upload_video(bili: &BiliBili, path: &Path) -> Result<Video, AppError> {
    let file = VideoFile::new(path).map_err(|_| {
        AppError::input(
            ErrorCode::InvalidMedia,
            "video file could not be opened for upload",
        )
    })?;
    let parcel = line::bldsa().pre_upload(bili, file).await.map_err(|_| {
        AppError::uncertain(
            ErrorCode::UploadUncertain,
            "video pre-upload failed; inspect the account before retrying",
        )
    })?;
    parcel
        .upload(StatelessClient::default(), 3, |stream| {
            stream.map(|chunk| {
                chunk
                    .map(|bytes| {
                        let len = bytes.len();
                        (bytes, len)
                    })
                    .map_err(Into::into)
            })
        })
        .await
        .map_err(|_| {
            AppError::uncertain(
                ErrorCode::UploadUncertain,
                "video upload failed or its result is uncertain; do not blindly retry",
            )
        })
}

pub async fn upload_cover(bili: &BiliBili, path: &Path) -> Result<String, AppError> {
    let bytes = std::fs::read(path)
        .map_err(|_| AppError::input(ErrorCode::InvalidMedia, "cover file could not be read"))?;
    bili.cover_up(&bytes).await.map_err(|_| {
        AppError::uncertain(
            ErrorCode::UploadUncertain,
            "cover upload failed; do not blindly retry the whole operation",
        )
    })
}

pub async fn submit_create(bili: &BiliBili, studio: &Studio) -> Result<Value, AppError> {
    let response = bili.submit_by_app(studio, None).await.map_err(|_| {
        AppError::uncertain(
            ErrorCode::SubmitUncertain,
            "稿件 submission failed or its result is uncertain; do not blindly retry",
        )
    })?;
    serde_json::to_value(response)
        .map_err(|_| AppError::internal("could not normalize the submission result"))
}

pub async fn submit_edit(bili: &BiliBili, studio: &Studio) -> Result<Value, AppError> {
    bili.edit_by_app(studio, None).await.map_err(|_| {
        AppError::uncertain(
            ErrorCode::SubmitUncertain,
            "稿件 edit failed or its result is uncertain; do not blindly retry",
        )
    })
}

pub struct NewStudio<'a> {
    pub title: &'a str,
    pub description: Option<&'a str>,
    pub tags: &'a [String],
    pub category_id: u16,
    pub copyright: u8,
    pub source: Option<&'a str>,
    pub cover: &'a str,
    pub visibility: Visibility,
    pub video: Video,
}

pub fn create_studio(input: NewStudio<'_>) -> Result<Studio, AppError> {
    serde_json::from_value(serde_json::json!({
        "copyright": input.copyright,
        "source": input.source.unwrap_or(""),
        "tid": input.category_id,
        "cover": input.cover,
        "title": input.title,
        "desc": input.description.unwrap_or(""),
        "tag": input.tags.join(","),
        "is_only_self": input.visibility.value(),
        "videos": [input.video]
    }))
    .map_err(|_| AppError::internal("could not construct biliup submission data"))
}

pub fn response_bvid(value: &Value) -> Option<String> {
    value
        .pointer("/data/bvid")
        .and_then(Value::as_str)
        .or_else(|| value.pointer("/data/resource_id").and_then(Value::as_str))
        .filter(|v| v.starts_with("BV"))
        .map(ToOwned::to_owned)
}
