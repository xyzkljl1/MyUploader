use serde::{Deserialize, Serialize};
use serde_json::Value;
use uuid::Uuid;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(tag = "operation", rename_all = "kebab-case", deny_unknown_fields)]
pub enum Request {
    Create {
        #[serde(rename = "requestId")]
        request_id: Uuid,
        #[serde(rename = "videoPath")]
        video_path: String,
        title: String,
        description: Option<String>,
        tags: Vec<String>,
        #[serde(rename = "categoryId")]
        category_id: u16,
        copyright: Copyright,
        source: Option<String>,
        #[serde(rename = "coverPath")]
        cover_path: Option<String>,
        #[serde(rename = "partTitle")]
        part_title: Option<String>,
        visibility: Option<Visibility>,
        chapters: Option<Vec<Chapter>>,
    },
    UpdateVideo {
        #[serde(rename = "requestId")]
        request_id: Uuid,
        bvid: String,
        #[serde(rename = "videoPath")]
        video_path: String,
        cid: Option<u64>,
        #[serde(rename = "partTitle")]
        part_title: Option<String>,
        visibility: Option<Visibility>,
        chapters: Option<Vec<Chapter>>,
    },
    UpdateInfo {
        #[serde(rename = "requestId")]
        request_id: Uuid,
        bvid: String,
        title: Option<String>,
        description: Option<String>,
        tags: Option<Vec<String>>,
        #[serde(rename = "categoryId")]
        category_id: Option<u16>,
        copyright: Option<Copyright>,
        source: Option<String>,
        #[serde(rename = "coverPath")]
        cover_path: Option<String>,
        visibility: Option<Visibility>,
    },
    SetChapters {
        #[serde(rename = "requestId")]
        request_id: Uuid,
        bvid: String,
        cid: Option<u64>,
        chapters: Vec<Chapter>,
    },
}

impl Request {
    pub fn request_id(&self) -> Uuid {
        match self {
            Self::Create { request_id, .. }
            | Self::UpdateVideo { request_id, .. }
            | Self::UpdateInfo { request_id, .. }
            | Self::SetChapters { request_id, .. } => *request_id,
        }
    }

    pub fn operation(&self) -> &'static str {
        match self {
            Self::Create { .. } => "create",
            Self::UpdateVideo { .. } => "update-video",
            Self::UpdateInfo { .. } => "update-info",
            Self::SetChapters { .. } => "set-chapters",
        }
    }

    pub fn bvid(&self) -> Option<&str> {
        match self {
            Self::UpdateVideo { bvid, .. }
            | Self::UpdateInfo { bvid, .. }
            | Self::SetChapters { bvid, .. } => Some(bvid),
            Self::Create { .. } => None,
        }
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "lowercase")]
pub enum Copyright {
    Original,
    Reprint,
}

impl Copyright {
    pub fn value(self) -> u8 {
        match self {
            Self::Original => 1,
            Self::Reprint => 2,
        }
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "kebab-case")]
pub enum Visibility {
    Public,
    SelfOnly,
}

impl Visibility {
    pub fn value(self) -> u8 {
        match self {
            Self::Public => 0,
            Self::SelfOnly => 1,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Chapter {
    pub start_seconds: u64,
    pub title: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MediaFingerprint {
    pub path: String,
    pub size: u64,
    pub sha256: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PartSnapshot {
    pub index: usize,
    pub cid: u64,
    pub title: String,
    pub filename: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct RemoteSnapshot {
    pub bvid: String,
    pub aid: u64,
    pub title: String,
    pub description: String,
    pub category_id: u16,
    pub tags: String,
    pub cover: String,
    pub copyright: u8,
    pub source: String,
    pub visibility: Visibility,
    pub parts: Vec<PartSnapshot>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TargetPart {
    pub index: usize,
    pub cid: u64,
    pub title: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Plan {
    pub request: Request,
    pub created_at_epoch_seconds: u64,
    pub expires_at_epoch_seconds: u64,
    pub media: Option<MediaFingerprint>,
    pub cover: Option<MediaFingerprint>,
    pub remote: Option<RemoteSnapshot>,
    pub target_part: Option<TargetPart>,
    pub changes: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PlanEnvelope {
    pub plan: Plan,
    pub plan_sha256: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Receipt {
    pub request_id: Uuid,
    pub operation: String,
    pub status: String,
    pub stage: String,
    pub bvid: Option<String>,
    pub target_cid: Option<u64>,
    pub error_code: Option<super::error::ErrorCode>,
}
