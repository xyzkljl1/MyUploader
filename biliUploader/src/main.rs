use bili_uploader::backend::{
    NewStudio, create_studio, fetch_editable, fetch_snapshot, open_credentials, response_bvid,
    submit_create, submit_edit, upload_cover, upload_video,
};
use bili_uploader::error::{AppError, ErrorCode};
use bili_uploader::model::{Plan, Receipt, RemoteSnapshot, Request, Visibility};
use bili_uploader::plan::{
    build_plan, envelope, read_and_verify, verify_local, verify_remote, write_new,
};
use bili_uploader::validation::validate_and_normalize;
use clap::{Args, Parser, Subcommand};
use fs2::FileExt;
use futures::FutureExt;
use qrcode::QrCode;
use qrcode::render::unicode;
use serde::Serialize;
use serde_json::json;
use std::fs::{File, OpenOptions};
use std::io::Write;
use std::panic::AssertUnwindSafe;
use std::path::{Path, PathBuf};

#[derive(Parser)]
#[command(
    name = "bili-uploader",
    version,
    about = "Guarded Bilibili publishing via biliup"
)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    Login {
        #[arg(long, value_name = "PATH")]
        config: Option<PathBuf>,
    },
    Inspect {
        #[arg(long)]
        bvid: String,
        #[arg(long, value_name = "PATH")]
        config: Option<PathBuf>,
    },
    Create(ActionArgs),
    UpdateVideo(ActionArgs),
    UpdateInfo(ActionArgs),
    SetChapters(ActionArgs),
}

#[derive(Args)]
struct ActionArgs {
    #[arg(long)]
    request: PathBuf,
    #[arg(long, value_name = "PATH")]
    config: Option<PathBuf>,
    #[arg(long, conflicts_with = "execute")]
    dry_run: bool,
    #[arg(long, conflicts_with = "dry_run")]
    execute: bool,
    #[arg(long)]
    plan: PathBuf,
    #[arg(long)]
    confirm: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Output<T: Serialize> {
    status: &'static str,
    data: T,
}

#[tokio::main]
async fn main() {
    std::panic::set_hook(Box::new(|_| {}));
    let result = AssertUnwindSafe(Box::pin(run())).catch_unwind().await;
    match result {
        Ok(Ok(())) => {}
        Ok(Err(error)) => {
            print_error(&error);
            std::process::exit(error.exit_code);
        }
        Err(_) => {
            let error = AppError::internal(
                "unexpected internal failure; no sensitive diagnostic data was printed",
            );
            print_error(&error);
            std::process::exit(error.exit_code);
        }
    }
}

async fn run() -> Result<(), AppError> {
    let cli = match Cli::try_parse() {
        Ok(cli) => cli,
        Err(error)
            if matches!(
                error.kind(),
                clap::error::ErrorKind::DisplayHelp | clap::error::ErrorKind::DisplayVersion
            ) =>
        {
            print!("{error}");
            return Ok(());
        }
        Err(error) => {
            return Err(AppError::input(
                ErrorCode::InvalidArguments,
                error.to_string(),
            ));
        }
    };
    match cli.command {
        Command::Login { config } => login(&resolve_config_path(config)?).await,
        Command::Inspect { bvid, config } => inspect(&bvid, &resolve_config_path(config)?).await,
        Command::Create(args) => action("create", args).await,
        Command::UpdateVideo(args) => action("update-video", args).await,
        Command::UpdateInfo(args) => action("update-info", args).await,
        Command::SetChapters(args) => action("set-chapters", args).await,
    }
}

async fn login(config: &Path) -> Result<(), AppError> {
    let credential = biliup::uploader::credential::Credential::new(None);
    let value = credential.get_qrcode().await.map_err(|_| {
        AppError::environment(
            ErrorCode::NetworkError,
            "could not obtain a Bilibili login QR code",
        )
    })?;
    let url = value
        .pointer("/data/url")
        .and_then(serde_json::Value::as_str)
        .ok_or_else(|| {
            AppError::environment(
                ErrorCode::CredentialError,
                "Bilibili did not return a usable login QR code",
            )
        })?;
    let qr = QrCode::new(url.replace("https", "http")).map_err(|_| {
        AppError::environment(
            ErrorCode::CredentialError,
            "could not render the Bilibili login QR code",
        )
    })?;
    let image = qr
        .render::<unicode::Dense1x2>()
        .dark_color(unicode::Dense1x2::Light)
        .light_color(unicode::Dense1x2::Dark)
        .build();
    eprintln!("Use the Bilibili mobile app to scan and confirm this QR code:\n{image}");
    let info = credential.login_by_qrcode(value).await.map_err(|_| {
        AppError::environment(
            ErrorCode::CredentialError,
            "Bilibili QR login failed or expired; run login again",
        )
    })?;
    let parent = config
        .parent()
        .filter(|value| !value.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    if !parent.is_dir() {
        return Err(AppError::environment(
            ErrorCode::CredentialError,
            "the configuration directory does not exist",
        ));
    }
    let bytes = serde_json::to_vec_pretty(&info).map_err(|_| {
        AppError::environment(
            ErrorCode::CredentialError,
            "the login result could not be saved",
        )
    })?;
    let mut file = OpenOptions::new()
        .create(true)
        .truncate(true)
        .write(true)
        .open(config)
        .map_err(|_| {
            AppError::environment(
                ErrorCode::CredentialError,
                "the configuration file could not be written",
            )
        })?;
    file.write_all(&bytes).map_err(|_| {
        AppError::environment(
            ErrorCode::CredentialError,
            "the configuration file could not be written",
        )
    })?;
    file.sync_all().map_err(|_| {
        AppError::environment(
            ErrorCode::CredentialError,
            "the configuration file could not be flushed",
        )
    })?;
    print_json(&Output {
        status: "login_success",
        data: json!({ "configPath": config }),
    })
}

fn resolve_config_path(explicit: Option<PathBuf>) -> Result<PathBuf, AppError> {
    if let Some(path) = explicit {
        if path.file_name().and_then(|value| value.to_str()) != Some("config.json") {
            return Err(AppError::input(
                ErrorCode::InvalidArguments,
                "the configuration file name must be config.json",
            ));
        }
        return Ok(path);
    }
    let executable = std::env::current_exe()
        .map_err(|_| AppError::internal("cannot locate the executable for default config"))?;
    default_config_path(&executable)
}

fn default_config_path(executable: &Path) -> Result<PathBuf, AppError> {
    let executable_dir = executable
        .parent()
        .ok_or_else(|| AppError::internal("cannot locate the executable directory"))?;
    let is_cargo_profile = executable_dir
        .file_name()
        .and_then(|value| value.to_str())
        .is_some_and(|value| matches!(value, "debug" | "release"));
    let tool_dir = if is_cargo_profile
        && executable_dir
            .parent()
            .and_then(Path::file_name)
            .and_then(|value| value.to_str())
            == Some("target")
    {
        executable_dir
            .parent()
            .and_then(Path::parent)
            .ok_or_else(|| AppError::internal("cannot locate the tool directory"))?
    } else {
        executable_dir
    };
    Ok(tool_dir.join("config.json"))
}

async fn inspect(bvid: &str, credentials: &Path) -> Result<(), AppError> {
    bili_uploader::validation::validate_bvid(bvid)?;
    let bili = open_credentials(credentials)?;
    let snapshot = fetch_snapshot(&bili, bvid).await?;
    print_json(&Output {
        status: "inspected",
        data: snapshot,
    })
}

async fn action(expected_operation: &str, args: ActionArgs) -> Result<(), AppError> {
    if args.dry_run == args.execute {
        return Err(AppError::input(
            ErrorCode::InvalidArguments,
            "specify exactly one of --dry-run or --execute",
        ));
    }
    if args.dry_run && args.confirm.is_some() {
        return Err(AppError::input(
            ErrorCode::InvalidArguments,
            "--confirm is only valid with --execute",
        ));
    }
    if args.execute && args.confirm.is_none() {
        return Err(AppError::input(
            ErrorCode::InvalidArguments,
            "--execute requires --confirm",
        ));
    }

    let config = resolve_config_path(args.config.clone())?;
    let request = load_request(&args.request)?;
    let base = args.request.parent().unwrap_or_else(|| Path::new("."));
    let request = validate_and_normalize(request, base)?;
    if request.operation() != expected_operation {
        return Err(AppError::input(
            ErrorCode::InvalidRequest,
            "request operation does not match the command",
        ));
    }

    if args.dry_run {
        let bili = open_credentials(&config)?;
        let plan = build_plan(request, Some(&bili)).await?;
        validate_semantics(&plan)?;
        let envelope = envelope(plan)?;
        write_new(&args.plan, &envelope)?;
        return print_json(&Output {
            status: "dry_run_ok",
            data: envelope,
        });
    }

    let saved = read_and_verify(&args.plan, args.confirm.as_deref().unwrap_or_default())?;
    if saved.plan.request != request {
        return Err(AppError::input(
            ErrorCode::PlanMismatch,
            "request differs from the dry-run plan",
        ));
    }
    verify_local(&saved.plan)?;
    validate_semantics(&saved.plan)?;
    let bili = open_credentials(&config)?;
    let current = match request.bvid() {
        Some(bvid) => Some(fetch_snapshot(&bili, bvid).await?),
        None => None,
    };
    verify_remote(&saved.plan.remote, &current)?;

    let state = state_directory()?;
    std::fs::create_dir_all(&state).map_err(|_| {
        AppError::environment(
            ErrorCode::InternalError,
            "state directory could not be created",
        )
    })?;
    let lock_path = state.join("publish.lock");
    let lock = OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(lock_path)
        .map_err(|_| {
            AppError::environment(ErrorCode::Busy, "publisher lock could not be opened")
        })?;
    lock.try_lock_exclusive().map_err(|_| {
        AppError::environment(ErrorCode::Busy, "another publish operation is active")
    })?;
    let receipt_path = state.join(format!("{}.receipt.json", request.request_id()));
    if receipt_path.exists() {
        return Err(AppError::input(
            ErrorCode::ReceiptExists,
            "this requestId already has a receipt; inspect it instead of retrying",
        ));
    }
    let mut receipt = Receipt {
        request_id: request.request_id(),
        operation: request.operation().to_string(),
        status: "in_progress".into(),
        stage: "ready".into(),
        bvid: request.bvid().map(ToOwned::to_owned),
        target_cid: saved.plan.target_part.as_ref().map(|part| part.cid),
        error_code: None,
    };
    save_new_receipt(&receipt_path, &receipt)?;

    let result = execute(&bili, &saved.plan, &mut receipt, &receipt_path).await;
    if let Err(error) = &result {
        receipt.status = if error.exit_code == 4 {
            "partial_or_uncertain"
        } else {
            "failed"
        }
        .into();
        receipt.error_code = Some(error.code);
        let _ = replace_receipt(&receipt_path, &receipt);
    }
    result?;
    receipt.status = "success".into();
    receipt.stage = "verified".into();
    receipt.error_code = None;
    replace_receipt(&receipt_path, &receipt)?;
    print_json(&Output {
        status: "success",
        data: receipt,
    })
}

async fn execute(
    bili: &biliup::uploader::bilibili::BiliBili,
    plan: &Plan,
    receipt: &mut Receipt,
    receipt_path: &Path,
) -> Result<(), AppError> {
    match &plan.request {
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
            visibility,
            ..
        } => {
            let cover = if let Some(path) = cover_path {
                set_stage(receipt, receipt_path, "uploading-cover")?;
                upload_cover(bili, Path::new(path)).await?
            } else {
                String::new()
            };
            set_stage(receipt, receipt_path, "uploading-video")?;
            let mut video = upload_video(bili, Path::new(video_path)).await?;
            if let Some(part_title) = part_title {
                video.title = Some(part_title.clone());
            }
            let expected_filename = video.filename.clone();
            let studio = create_studio(NewStudio {
                title,
                description: description.as_deref(),
                tags,
                category_id: *category_id,
                copyright: copyright.value(),
                source: source.as_deref(),
                cover: &cover,
                video,
                visibility: visibility.unwrap_or(Visibility::Public),
            })?;
            set_stage(receipt, receipt_path, "submitting")?;
            let response = submit_create(bili, &studio).await?;
            let bvid = response_bvid(&response).ok_or_else(|| {
                AppError::uncertain(
                    ErrorCode::SubmitUncertain,
                    "submission returned no usable BVID; reconcile remotely before retrying",
                )
            })?;
            receipt.bvid = Some(bvid.clone());
            set_stage(receipt, receipt_path, "verifying")?;
            let _ = retry_verified_snapshot(bili, &bvid, |snapshot| {
                if snapshot.title == *title
                    && snapshot.parts.len() == 1
                    && snapshot.parts[0].filename == expected_filename
                    && snapshot.visibility == visibility.unwrap_or(Visibility::Public)
                {
                    Ok(())
                } else {
                    Err(verify_mismatch(
                        "created稿件 did not match the planned title and video",
                    ))
                }
            })
            .await?;
        }
        Request::UpdateVideo {
            bvid,
            video_path,
            part_title,
            visibility,
            ..
        } => {
            let target = plan
                .target_part
                .as_ref()
                .ok_or_else(|| AppError::internal("missing target part"))?;
            set_stage(receipt, receipt_path, "uploading-video")?;
            let mut video = upload_video(bili, Path::new(video_path)).await?;
            video.title = Some(part_title.clone().unwrap_or_else(|| target.title.clone()));
            let (latest, mut studio) = fetch_editable(bili, bvid).await.map_err(|_| {
                verify_mismatch(
                    "video uploaded, but the target稿件 could not be re-read; do not retry",
                )
            })?;
            ensure_submit_target(plan, &latest, true)?;
            if studio.videos.len() != latest.parts.len() || target.index >= studio.videos.len() {
                return Err(verify_mismatch(
                    "video uploaded, but the editable part list no longer matches the plan",
                ));
            }
            video.desc = studio.videos[target.index].desc.clone();
            let expected_filename = video.filename.clone();
            studio.videos[target.index] = video;
            if let Some(value) = visibility {
                studio.is_only_self = Some(value.value());
            }
            set_stage(receipt, receipt_path, "submitting")?;
            let _ = submit_edit(bili, &studio).await?;
            set_stage(receipt, receipt_path, "verifying")?;
            let snapshot = retry_verified_snapshot(bili, bvid, |snapshot| {
                verify_video_update(
                    plan.remote.as_ref().unwrap(),
                    snapshot,
                    target.index,
                    &expected_filename,
                    part_title.as_deref(),
                    *visibility,
                )
            })
            .await?;
            receipt.target_cid = snapshot.parts.get(target.index).map(|part| part.cid);
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
            let cover_uploaded = cover_path.is_some();
            let uploaded_cover = if let Some(path) = cover_path {
                set_stage(receipt, receipt_path, "uploading-cover")?;
                Some(upload_cover(bili, Path::new(path)).await?)
            } else {
                None
            };
            let (latest, mut studio) = fetch_editable(bili, bvid).await.map_err(|_| {
                if cover_uploaded {
                    verify_mismatch(
                        "cover uploaded, but the target稿件 could not be re-read; do not retry",
                    )
                } else {
                    AppError::environment(
                        ErrorCode::NetworkError,
                        "could not load editable稿件 data",
                    )
                }
            })?;
            ensure_submit_target(plan, &latest, cover_uploaded)?;
            if let Some(value) = title {
                studio.title = value.clone();
            }
            if let Some(value) = description {
                studio.desc = value.clone();
            }
            if let Some(value) = tags {
                studio.tag = value.join(",");
            }
            if let Some(value) = category_id {
                studio.tid = *value;
            }
            if let Some(value) = copyright {
                studio.copyright = value.value();
            }
            if let Some(value) = source {
                studio.source = value.clone();
            }
            if matches!(copyright, Some(bili_uploader::model::Copyright::Original)) {
                studio.source.clear();
            }
            if let Some(value) = uploaded_cover {
                studio.cover = value;
            }
            if let Some(value) = visibility {
                studio.is_only_self = Some(value.value());
            }
            let expected_cover = studio.cover.clone();
            set_stage(receipt, receipt_path, "submitting")?;
            let _ = submit_edit(bili, &studio).await?;
            set_stage(receipt, receipt_path, "verifying")?;
            let _ = retry_verified_snapshot(bili, bvid, |snapshot| {
                verify_info_update(&plan.request, snapshot, &expected_cover)
            })
            .await?;
        }
        Request::SetChapters { .. } => {
            return Err(AppError::input(
                ErrorCode::UnsupportedChapterWrite,
                "native chapter writing is unavailable",
            ));
        }
    }
    Ok(())
}

fn ensure_submit_target(
    plan: &Plan,
    latest: &RemoteSnapshot,
    write_already_started: bool,
) -> Result<(), AppError> {
    if plan.remote.as_ref() == Some(latest) {
        return Ok(());
    }
    if write_already_started {
        Err(verify_mismatch(
            "remote稿件 changed after a file upload; no edit was submitted and the whole operation must not be retried blindly",
        ))
    } else {
        Err(AppError::input(
            ErrorCode::PlanMismatch,
            "remote稿件 changed before submission; run dry-run again",
        ))
    }
}

fn validate_semantics(plan: &Plan) -> Result<(), AppError> {
    if let (
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
        },
        Some(remote),
    ) = (&plan.request, &plan.remote)
    {
        let final_copyright = copyright.map_or(remote.copyright, |v| v.value());
        let final_source = if matches!(copyright, Some(bili_uploader::model::Copyright::Original)) {
            ""
        } else {
            source.as_deref().unwrap_or(&remote.source)
        };
        if final_copyright == 2 && final_source.is_empty() {
            return Err(AppError::input(
                ErrorCode::InvalidRequest,
                "reprint稿件 requires a non-empty source",
            ));
        }
        if final_copyright == 1 && source.as_ref().is_some_and(|value| !value.is_empty()) {
            return Err(AppError::input(
                ErrorCode::InvalidRequest,
                "source cannot be set on an original稿件",
            ));
        }
        let changed = title.as_ref().is_some_and(|v| v != &remote.title)
            || description
                .as_ref()
                .is_some_and(|v| v != &remote.description)
            || tags.as_ref().is_some_and(|v| v.join(",") != remote.tags)
            || category_id.is_some_and(|v| v != remote.category_id)
            || copyright.is_some_and(|v| v.value() != remote.copyright)
            || source.as_ref().is_some_and(|v| v != &remote.source)
            || cover_path.is_some()
            || visibility.is_some_and(|v| remote.visibility != v);
        if !changed {
            return Err(AppError::input(
                ErrorCode::InvalidRequest,
                "update-info would make no change",
            ));
        }
    }
    Ok(())
}

fn verify_video_update(
    before: &RemoteSnapshot,
    after: &RemoteSnapshot,
    index: usize,
    filename: &str,
    part_title: Option<&str>,
    visibility: Option<Visibility>,
) -> Result<(), AppError> {
    if before.bvid != after.bvid
        || before.parts.len() != after.parts.len()
        || after
            .parts
            .get(index)
            .is_none_or(|p| p.filename != filename)
        || visibility.is_some_and(|value| after.visibility != value)
    {
        return Err(AppError::uncertain(
            ErrorCode::VerifyMismatch,
            "updated part could not be verified; do not re-upload",
        ));
    }
    if let Some(title) = part_title
        && after.parts[index].title != title
    {
        return Err(AppError::uncertain(
            ErrorCode::VerifyMismatch,
            "updated part title could not be verified; do not re-upload",
        ));
    }
    for part in &before.parts {
        if part.index != index
            && after
                .parts
                .get(part.index)
                .is_none_or(|new| new.cid != part.cid || new.filename != part.filename)
        {
            return Err(AppError::uncertain(
                ErrorCode::VerifyMismatch,
                "a non-target part changed unexpectedly; do not retry",
            ));
        }
    }
    Ok(())
}

fn verify_info_update(
    request: &Request,
    after: &RemoteSnapshot,
    expected_cover: &str,
) -> Result<(), AppError> {
    let Request::UpdateInfo {
        title,
        description,
        tags,
        category_id,
        copyright,
        source,
        cover_path,
        visibility,
        ..
    } = request
    else {
        return Err(AppError::internal("invalid verification request"));
    };
    let matches = title.as_ref().is_none_or(|v| v == &after.title)
        && description.as_ref().is_none_or(|v| v == &after.description)
        && tags.as_ref().is_none_or(|v| v.join(",") == after.tags)
        && category_id.is_none_or(|v| v == after.category_id)
        && copyright.is_none_or(|v| v.value() == after.copyright)
        && source.as_ref().is_none_or(|v| v == &after.source)
        && cover_path
            .as_ref()
            .is_none_or(|_| expected_cover == after.cover)
        && visibility.is_none_or(|value| after.visibility == value);
    if matches {
        Ok(())
    } else {
        Err(AppError::uncertain(
            ErrorCode::VerifyMismatch,
            "updated稿件 information could not be verified; do not retry",
        ))
    }
}

async fn retry_verified_snapshot<F>(
    bili: &biliup::uploader::bilibili::BiliBili,
    bvid: &str,
    verify: F,
) -> Result<RemoteSnapshot, AppError>
where
    F: Fn(&RemoteSnapshot) -> Result<(), AppError>,
{
    let mut last_mismatch = None;
    for attempt in 0..5 {
        if let Ok(snapshot) = fetch_snapshot(bili, bvid).await {
            match verify(&snapshot) {
                Ok(()) => return Ok(snapshot),
                Err(error) => last_mismatch = Some(error),
            }
        }
        if attempt < 4 {
            tokio::time::sleep(std::time::Duration::from_secs(2)).await;
        }
    }
    Err(last_mismatch.unwrap_or_else(|| verify_mismatch("could not read back and verify稿件")))
}

fn verify_mismatch(message: impl Into<String>) -> AppError {
    AppError::uncertain(ErrorCode::VerifyMismatch, message)
}

fn load_request(path: &Path) -> Result<Request, AppError> {
    let metadata = path
        .metadata()
        .map_err(|_| AppError::input(ErrorCode::InvalidRequest, "request file does not exist"))?;
    if !metadata.is_file() || metadata.len() > 1024 * 1024 {
        return Err(AppError::input(
            ErrorCode::InvalidRequest,
            "request must be a regular JSON file no larger than 1 MiB",
        ));
    }
    let bytes = std::fs::read(path).map_err(|_| {
        AppError::input(ErrorCode::InvalidRequest, "request file could not be read")
    })?;
    serde_json::from_slice(&bytes).map_err(|_| {
        AppError::input(
            ErrorCode::InvalidRequest,
            "request JSON is invalid, contains duplicate/unknown fields, or has wrong types",
        )
    })
}

fn state_directory() -> Result<PathBuf, AppError> {
    let executable =
        std::env::current_exe().map_err(|_| AppError::internal("cannot locate executable"))?;
    Ok(executable
        .parent()
        .ok_or_else(|| AppError::internal("cannot locate executable directory"))?
        .join(".bili-state"))
}

fn save_new_receipt(path: &Path, value: &Receipt) -> Result<(), AppError> {
    let file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(path)
        .map_err(|_| AppError::input(ErrorCode::ReceiptExists, "receipt already exists"))?;
    write_receipt(file, value)
}

fn replace_receipt(path: &Path, value: &Receipt) -> Result<(), AppError> {
    let file = OpenOptions::new()
        .write(true)
        .truncate(true)
        .open(path)
        .map_err(|_| {
            AppError::environment(ErrorCode::InternalError, "receipt could not be updated")
        })?;
    write_receipt(file, value)
}

fn write_receipt(mut file: File, value: &Receipt) -> Result<(), AppError> {
    let bytes = serde_json::to_vec_pretty(value)
        .map_err(|_| AppError::internal("receipt could not be serialized"))?;
    file.write_all(&bytes)
        .and_then(|_| file.sync_all())
        .map_err(|_| {
            AppError::environment(ErrorCode::InternalError, "receipt could not be written")
        })
}

fn set_stage(receipt: &mut Receipt, path: &Path, stage: &str) -> Result<(), AppError> {
    receipt.stage = stage.to_string();
    replace_receipt(path, receipt)
}

fn print_json<T: Serialize>(value: &T) -> Result<(), AppError> {
    let text = serde_json::to_string_pretty(value)
        .map_err(|_| AppError::internal("result could not be serialized"))?;
    println!("{text}");
    Ok(())
}

fn print_error(error: &AppError) {
    let value = json!({ "status": "error", "errorCode": error.code, "message": error.message });
    println!(
        "{}",
        serde_json::to_string_pretty(&value)
            .unwrap_or_else(|_| "{\"status\":\"error\",\"errorCode\":\"INTERNAL_ERROR\"}".into())
    );
}

#[cfg(test)]
mod tests {
    use super::default_config_path;
    use std::path::Path;

    #[test]
    fn cargo_build_uses_the_tool_directory_for_default_config() {
        let path = default_config_path(Path::new(
            "C:/repo/MyUploader/biliUploader/target/release/bili-uploader.exe",
        ))
        .unwrap();
        assert_eq!(
            path,
            Path::new("C:/repo/MyUploader/biliUploader/config.json")
        );
    }

    #[test]
    fn installed_binary_uses_its_own_directory_for_default_config() {
        let path = default_config_path(Path::new("C:/Tools/bili-uploader.exe")).unwrap();
        assert_eq!(path, Path::new("C:/Tools/config.json"));
    }
}
