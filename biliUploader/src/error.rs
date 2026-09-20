use serde::Serialize;
use std::fmt::{Display, Formatter};

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum ErrorCode {
    InvalidArguments,
    InvalidRequest,
    InvalidMedia,
    UnsupportedChapterWrite,
    TargetMismatch,
    PlanMismatch,
    PlanExpired,
    PlanExists,
    ReceiptExists,
    Busy,
    CredentialError,
    NetworkError,
    UploadUncertain,
    SubmitUncertain,
    VerifyMismatch,
    InternalError,
}

#[derive(Debug)]
pub struct AppError {
    pub code: ErrorCode,
    pub message: String,
    pub exit_code: i32,
}

impl AppError {
    pub fn input(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
            exit_code: 2,
        }
    }

    pub fn environment(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
            exit_code: 3,
        }
    }

    pub fn uncertain(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
            exit_code: 4,
        }
    }

    pub fn internal(message: impl Into<String>) -> Self {
        Self {
            code: ErrorCode::InternalError,
            message: message.into(),
            exit_code: 5,
        }
    }
}

impl Display for AppError {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for AppError {}
