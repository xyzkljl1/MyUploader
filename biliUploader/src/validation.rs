use crate::error::{AppError, ErrorCode};
use crate::model::{Chapter, MediaFingerprint, Request};
use sha2::{Digest, Sha256};
use std::fs::File;
use std::io::Read;
use std::path::{Path, PathBuf};

const MAX_VIDEO_BYTES: u64 = 20 * 1024 * 1024 * 1024;
const MAX_COVER_BYTES: u64 = 10 * 1024 * 1024;

pub fn validate_and_normalize(mut request: Request, base: &Path) -> Result<Request, AppError> {
    match &mut request {
        Request::Create {
            video_path,
            title,
            description,
            tags,
            category_id,
            copyright,
            source,
            cover_path,
            part_title,
            chapters,
            ..
        } => {
            reject_chapters(chapters.as_deref())?;
            validate_title(title, "title")?;
            validate_optional(description.as_deref(), "description", 2000)?;
            validate_tags(tags)?;
            if *category_id == 0 {
                return Err(invalid("categoryId must be positive"));
            }
            validate_optional(part_title.as_deref(), "partTitle", 80)?;
            match copyright {
                crate::model::Copyright::Original if source.is_some() => {
                    return Err(invalid("source is only valid for reprint"));
                }
                crate::model::Copyright::Reprint if source.as_deref().is_none_or(str::is_empty) => {
                    return Err(invalid("source is required for reprint"));
                }
                _ => {}
            }
            *video_path = normalize_path(base, video_path, false)?;
            if let Some(path) = cover_path {
                *path = normalize_path(base, path, true)?;
            }
        }
        Request::UpdateVideo {
            bvid,
            video_path,
            cid,
            part_title,
            chapters,
            ..
        } => {
            reject_chapters(chapters.as_deref())?;
            validate_bvid(bvid)?;
            if matches!(cid, Some(0)) {
                return Err(invalid("cid must be positive"));
            }
            validate_optional(part_title.as_deref(), "partTitle", 80)?;
            *video_path = normalize_path(base, video_path, false)?;
        }
        Request::UpdateInfo {
            bvid,
            title,
            description,
            tags,
            category_id,
            copyright,
            source,
            cover_path,
            visibility,
            ..
        } => {
            validate_bvid(bvid)?;
            if title.is_none()
                && description.is_none()
                && tags.is_none()
                && category_id.is_none()
                && copyright.is_none()
                && source.is_none()
                && cover_path.is_none()
                && visibility.is_none()
            {
                return Err(invalid("update-info requires at least one changed field"));
            }
            if let Some(value) = title {
                validate_title(value, "title")?;
            }
            validate_optional(description.as_deref(), "description", 2000)?;
            if let Some(value) = tags {
                validate_tags(value)?;
            }
            if matches!(category_id, Some(0)) {
                return Err(invalid("categoryId must be positive"));
            }
            if let Some(path) = cover_path {
                *path = normalize_path(base, path, true)?;
            }
        }
        Request::SetChapters { chapters, .. } => {
            validate_chapters(chapters)?;
            return Err(AppError::input(
                ErrorCode::UnsupportedChapterWrite,
                "biliup v1.2.2 does not expose a reliable native chapter-write interface",
            ));
        }
    }
    Ok(request)
}

fn reject_chapters(chapters: Option<&[Chapter]>) -> Result<(), AppError> {
    if let Some(chapters) = chapters {
        validate_chapters(chapters)?;
        return Err(AppError::input(
            ErrorCode::UnsupportedChapterWrite,
            "native chapter writing is unavailable; the request was stopped before credentials or upload",
        ));
    }
    Ok(())
}

fn validate_chapters(chapters: &[Chapter]) -> Result<(), AppError> {
    if chapters.is_empty() {
        return Err(invalid("chapters must not be empty"));
    }
    if chapters[0].start_seconds != 0 {
        return Err(invalid("the first chapter must start at 0 seconds"));
    }
    let mut previous = None;
    for chapter in chapters {
        validate_title(&chapter.title, "chapter title")?;
        if previous.is_some_and(|value| chapter.start_seconds <= value) {
            return Err(invalid(
                "chapter startSeconds values must be strictly increasing",
            ));
        }
        previous = Some(chapter.start_seconds);
    }
    Ok(())
}

pub fn fingerprint(path: &str, cover: bool) -> Result<MediaFingerprint, AppError> {
    let path_ref = Path::new(path);
    let metadata = path_ref
        .metadata()
        .map_err(|_| invalid("media file cannot be read"))?;
    if !metadata.is_file() || metadata.len() == 0 {
        return Err(invalid("media path must be a non-empty regular file"));
    }
    let extension = path_ref
        .extension()
        .and_then(|v| v.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    let allowed = if cover {
        ["jpg", "jpeg", "png"].contains(&extension.as_str())
    } else {
        ["mp4", "flv", "mkv", "webm", "ts", "3gp"].contains(&extension.as_str())
    };
    if !allowed {
        return Err(AppError::input(
            ErrorCode::InvalidMedia,
            if cover {
                "cover must be JPG or PNG"
            } else {
                "unsupported video extension"
            },
        ));
    }
    let limit = if cover {
        MAX_COVER_BYTES
    } else {
        MAX_VIDEO_BYTES
    };
    if metadata.len() > limit {
        return Err(AppError::input(
            ErrorCode::InvalidMedia,
            "media file exceeds the configured safety limit",
        ));
    }
    let mut file = File::open(path_ref).map_err(|_| invalid("media file cannot be opened"))?;
    let mut hasher = Sha256::new();
    let mut buffer = vec![0u8; 1024 * 1024];
    loop {
        let count = file
            .read(&mut buffer)
            .map_err(|_| invalid("media file could not be fully read"))?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
    }
    Ok(MediaFingerprint {
        path: path.to_string(),
        size: metadata.len(),
        sha256: hex(&hasher.finalize()),
    })
}

pub fn validate_bvid(value: &str) -> Result<(), AppError> {
    let valid = value.len() == 12
        && value.starts_with("BV")
        && value.bytes().all(|b| b.is_ascii_alphanumeric());
    if valid {
        Ok(())
    } else {
        Err(invalid("bvid must be a 12-character BV identifier"))
    }
}

fn normalize_path(base: &Path, value: &str, cover: bool) -> Result<String, AppError> {
    if value.trim() != value || value.is_empty() {
        return Err(invalid(
            "file path must be non-empty and have no surrounding whitespace",
        ));
    }
    let input = PathBuf::from(value);
    let joined = if input.is_absolute() {
        input
    } else {
        base.join(input)
    };
    let canonical = joined
        .canonicalize()
        .map_err(|_| invalid("file path does not exist"))?;
    let text = canonical
        .to_str()
        .ok_or_else(|| invalid("file path must be valid Unicode"))?
        .to_string();
    let _ = fingerprint(&text, cover)?;
    Ok(text)
}

fn validate_title(value: &str, name: &str) -> Result<(), AppError> {
    if value.trim() != value
        || value.is_empty()
        || value.chars().count() > 80
        || value.chars().any(char::is_control)
    {
        return Err(invalid(format!(
            "{name} must contain 1-80 characters, no control or surrounding whitespace"
        )));
    }
    Ok(())
}

fn validate_optional(value: Option<&str>, name: &str, max: usize) -> Result<(), AppError> {
    if let Some(value) = value
        && (value.trim() != value
            || value.is_empty()
            || value.chars().count() > max
            || value.chars().any(|c| c == '\0'))
    {
        return Err(invalid(format!(
            "{name} must be non-empty when supplied, have no surrounding whitespace, and be at most {max} characters"
        )));
    }
    Ok(())
}

fn validate_tags(tags: &[String]) -> Result<(), AppError> {
    if tags.is_empty() || tags.len() > 10 {
        return Err(invalid("tags must contain 1-10 entries"));
    }
    for tag in tags {
        if tag.trim() != tag
            || tag.is_empty()
            || tag.chars().count() > 20
            || tag.contains(',')
            || tag.chars().any(char::is_control)
        {
            return Err(invalid(
                "each tag must contain 1-20 characters and no commas, control or surrounding whitespace",
            ));
        }
    }
    if tags.join(",").chars().count() > 200 {
        return Err(invalid("combined tags must not exceed 200 characters"));
    }
    Ok(())
}

fn invalid(message: impl Into<String>) -> AppError {
    AppError::input(ErrorCode::InvalidRequest, message)
}

pub fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
