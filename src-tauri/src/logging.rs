use chrono::Local;
use std::{
    fs::{self, OpenOptions},
    io::Write,
    sync::{Mutex, OnceLock},
};

static LOG_GATE: Mutex<()> = Mutex::new(());
static LOG_READY: OnceLock<()> = OnceLock::new();

pub fn init() {
    LOG_READY.get_or_init(|| {
        let _ = fs::create_dir_all(crate::config::app_dir());
    });
}

pub fn info(message: impl AsRef<str>) {
    write("INFO ", message.as_ref());
}

pub fn error(message: impl AsRef<str>) {
    write("ERROR", message.as_ref());
}

fn write(level: &str, message: &str) {
    init();
    let Ok(_guard) = LOG_GATE.lock() else {
        return;
    };
    let path = crate::config::app_dir().join("metriklite.log");
    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(path) {
        let sanitized = message.replace(['\r', '\n'], " ");
        let _ = writeln!(
            file,
            "{} [{}] {}",
            Local::now().format("%Y-%m-%d %H:%M:%S"),
            level,
            sanitized
        );
    }
}
