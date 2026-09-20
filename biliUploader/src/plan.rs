use crate::backend::fetch_snapshot;
use crate::error::{AppError, ErrorCode};
use crate::model::{MediaFingerprint, Plan, PlanEnvelope, RemoteSnapshot, Request, TargetPart};
use crate::validation::{fingerprint, hex};
use biliup::uploader::bilibili::BiliBili;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

const PLAN_LIFETIME_SECONDS: u64 = 15 * 60;

pub async fn build_plan(request: Request, bili: Option<&BiliBili>) -> Result<Plan, AppError> {
    let media = match &request {
        Request::Create { video_path, .. } | Request::UpdateVideo { video_path, .. } => {
            Some(fingerprint(video_path, false)?)
        }
        _ => None,
    };
    let cover = match &request {
        Request::Create { cover_path, .. } | Request::UpdateInfo { cover_path, .. } => cover_path
            .as_deref()
            .map(|p| fingerprint(p, true))
            .transpose()?,
        _ => None,
    };
    let remote = if let Some(bvid) = request.bvid() {
        Some(
            fetch_snapshot(
                bili.ok_or_else(|| AppError::internal("missing remote client"))?,
                bvid,
            )
            .await?,
        )
    } else {
        None
    };
    let target_part = match (&request, &remote) {
        (Request::UpdateVideo { cid, .. }, Some(remote)) => Some(select_part(remote, *cid)?),
        _ => None,
    };
    let changes = changes(&request, remote.as_ref(), target_part.as_ref());
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| AppError::internal("system clock is invalid"))?
        .as_secs();
    Ok(Plan {
        request,
        created_at_epoch_seconds: now,
        expires_at_epoch_seconds: now + PLAN_LIFETIME_SECONDS,
        media,
        cover,
        remote,
        target_part,
        changes,
    })
}

pub fn select_part(remote: &RemoteSnapshot, cid: Option<u64>) -> Result<TargetPart, AppError> {
    let part = match (remote.parts.as_slice(), cid) {
        ([only], None) => only,
        (_, None) => {
            return Err(AppError::input(
                ErrorCode::TargetMismatch,
                "target has multiple parts; cid is required",
            ));
        }
        (_, Some(cid)) => remote
            .parts
            .iter()
            .find(|part| part.cid == cid)
            .ok_or_else(|| {
                AppError::input(
                    ErrorCode::TargetMismatch,
                    "cid does not belong to the target BVID",
                )
            })?,
    };
    Ok(TargetPart {
        index: part.index,
        cid: part.cid,
        title: part.title.clone(),
    })
}

pub fn envelope(plan: Plan) -> Result<PlanEnvelope, AppError> {
    let bytes =
        serde_json::to_vec(&plan).map_err(|_| AppError::internal("could not serialize plan"))?;
    Ok(PlanEnvelope {
        plan_sha256: hex(&Sha256::digest(bytes)),
        plan,
    })
}

pub fn write_new(path: &Path, value: &PlanEnvelope) -> Result<(), AppError> {
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(path)
        .map_err(|_| {
            AppError::input(
                ErrorCode::PlanExists,
                "plan path already exists or cannot be created",
            )
        })?;
    let bytes = serde_json::to_vec_pretty(value)
        .map_err(|_| AppError::internal("could not serialize plan"))?;
    file.write_all(&bytes).map_err(|_| {
        AppError::environment(ErrorCode::InternalError, "plan could not be written")
    })?;
    file.sync_all()
        .map_err(|_| AppError::environment(ErrorCode::InternalError, "plan could not be flushed"))
}

pub fn read_and_verify(path: &Path, confirm: &str) -> Result<PlanEnvelope, AppError> {
    let mut bytes = Vec::new();
    File::open(path)
        .and_then(|mut f| f.read_to_end(&mut bytes))
        .map_err(|_| AppError::input(ErrorCode::PlanMismatch, "plan could not be read"))?;
    let envelope: PlanEnvelope = serde_json::from_slice(&bytes)
        .map_err(|_| AppError::input(ErrorCode::PlanMismatch, "plan is invalid"))?;
    let actual = hex(&Sha256::digest(
        serde_json::to_vec(&envelope.plan)
            .map_err(|_| AppError::internal("could not hash plan"))?,
    ));
    if actual != envelope.plan_sha256 || confirm != envelope.plan_sha256 {
        return Err(AppError::input(
            ErrorCode::PlanMismatch,
            "plan digest or confirmation does not match",
        ));
    }
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| AppError::internal("system clock is invalid"))?
        .as_secs();
    if now > envelope.plan.expires_at_epoch_seconds {
        return Err(AppError::input(
            ErrorCode::PlanExpired,
            "plan expired; run dry-run again",
        ));
    }
    Ok(envelope)
}

pub fn verify_local(plan: &Plan) -> Result<(), AppError> {
    if let Some(expected) = &plan.media {
        compare_fingerprint(expected, fingerprint(&expected.path, false)?)?;
    }
    if let Some(expected) = &plan.cover {
        compare_fingerprint(expected, fingerprint(&expected.path, true)?)?;
    }
    Ok(())
}

fn compare_fingerprint(
    expected: &MediaFingerprint,
    actual: MediaFingerprint,
) -> Result<(), AppError> {
    if *expected != actual {
        Err(AppError::input(
            ErrorCode::PlanMismatch,
            "a planned local file changed",
        ))
    } else {
        Ok(())
    }
}

pub fn verify_remote(
    expected: &Option<RemoteSnapshot>,
    actual: &Option<RemoteSnapshot>,
) -> Result<(), AppError> {
    if expected != actual {
        Err(AppError::input(
            ErrorCode::PlanMismatch,
            "remote稿件 changed after dry-run",
        ))
    } else {
        Ok(())
    }
}

fn changes(
    request: &Request,
    remote: Option<&RemoteSnapshot>,
    target: Option<&TargetPart>,
) -> Value {
    match request {
        Request::Create {
            title,
            category_id,
            tags,
            visibility,
            ..
        } => {
            json!({ "action": "create", "title": title, "categoryId": category_id, "tags": tags, "visibility": visibility.unwrap_or(crate::model::Visibility::Public) })
        }
        Request::UpdateVideo {
            part_title,
            visibility,
            ..
        } => {
            json!({ "action": "replace-part", "target": target, "partTitle": part_title, "visibility": visibility })
        }
        Request::UpdateInfo {
            title,
            description,
            tags,
            category_id,
            copyright,
            source,
            cover_path,
            visibility,
            ..
        } => json!({
            "action": "update-info",
            "before": remote,
            "requested": { "title": title, "description": description, "tags": tags, "categoryId": category_id, "copyright": copyright, "source": source, "coverPath": cover_path, "visibility": visibility }
        }),
        Request::SetChapters { .. } => json!({ "action": "unsupported" }),
    }
}
