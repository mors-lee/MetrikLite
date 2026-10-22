use ab_glyph::{point, Font, FontArc, Glyph, PxScale, Rect, ScaleFont};
use std::{
    fs,
    path::PathBuf,
    sync::{
        atomic::{AtomicU32, Ordering},
        OnceLock,
    },
};
use tauri::{
    image::Image, LogicalSize, Manager, PhysicalPosition, PhysicalSize, Position, WebviewWindow,
};

const ICON_SIZE: u32 = 32;
// Use the same deliberate UI scale for native geometry and WebView zoom.
// Shrinking only one side would reintroduce clipping on mixed-DPI displays.
const PANEL_UI_SCALE: f64 = 0.85;
static FONT: OnceLock<Option<FontArc>> = OnceLock::new();
static PANEL_CONTENT_HEIGHT: AtomicU32 = AtomicU32::new(0);

pub fn set_panel_content_height(height: f64) -> Result<bool, String> {
    if !height.is_finite() || !(200.0..=1200.0).contains(&height) {
        return Err("Invalid panel content height".into());
    }
    let height = height.ceil() as u32;
    Ok(PANEL_CONTENT_HEIGHT.swap(height, Ordering::AcqRel) != height)
}

fn panel_content_height(initial_height: f64) -> f64 {
    match PANEL_CONTENT_HEIGHT.load(Ordering::Acquire) {
        0 => initial_height,
        height => height as f64,
    }
}

pub fn render(percent: Option<i32>, light_glyphs: bool) -> Image<'static> {
    let text = percent
        .map(|value| value.clamp(0, 100).to_string())
        .unwrap_or_else(|| "!".into());
    let mut pixels = vec![0_u8; (ICON_SIZE * ICON_SIZE * 4) as usize];
    let Some(font) = load_font() else {
        draw_fallback(&mut pixels, light_glyphs);
        return Image::new_owned(pixels, ICON_SIZE, ICON_SIZE);
    };

    let glyphs = fitted_centered_glyphs(font, &text);
    let foreground = if percent.is_some_and(|value| value <= 20) {
        [220, 76, 72, 255]
    } else if light_glyphs {
        [250, 252, 255, 255]
    } else {
        [78, 130, 232, 255]
    };
    // 保持纯透明背景，不再绘制八方向描边。Windows 缩放托盘图标时，
    // 描边会被重采样成一圈白色/深色虚影。
    draw_glyphs(&mut pixels, font, &glyphs, foreground);
    Image::new_owned(pixels, ICON_SIZE, ICON_SIZE)
}

fn load_font() -> Option<&'static FontArc> {
    FONT.get_or_init(|| {
        let windows = std::env::var_os("SystemRoot")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(r"C:\Windows"));
        ["seguisb.ttf", "segoeuib.ttf", "arialbd.ttf"]
            .into_iter()
            .find_map(|name| {
                fs::read(windows.join("Fonts").join(name))
                    .ok()
                    .and_then(|bytes| FontArc::try_from_vec(bytes).ok())
            })
    })
    .as_ref()
}

fn fitted_centered_glyphs(font: &FontArc, text: &str) -> Vec<Glyph> {
    let probe_size = 32.0;
    let (_, probe_bounds) = layout_glyphs(font, text, probe_size);
    let target_width = if text.chars().count() == 1 {
        28.0
    } else {
        31.0
    };
    let target_height = 30.0;
    let scale = (target_width / probe_bounds.width().max(1.0))
        .min(target_height / probe_bounds.height().max(1.0))
        .clamp(0.4, 1.25);
    let (mut glyphs, bounds) = layout_glyphs(font, text, probe_size * scale);
    let x_shift = (ICON_SIZE as f32 - bounds.width()) / 2.0 - bounds.min.x;
    let y_shift = (ICON_SIZE as f32 - bounds.height()) / 2.0 - bounds.min.y - 0.1;
    for glyph in &mut glyphs {
        glyph.position.x += x_shift;
        glyph.position.y += y_shift;
    }
    glyphs
}

fn layout_glyphs(font: &FontArc, text: &str, size: f32) -> (Vec<Glyph>, Rect) {
    let scaled = font.as_scaled(PxScale::from(size));
    let mut cursor = 0.0;
    let mut previous = None;
    let mut glyphs = Vec::new();
    for character in text.chars() {
        let mut glyph = scaled.scaled_glyph(character);
        if let Some(previous_id) = previous {
            cursor += scaled.kern(previous_id, glyph.id);
        }
        glyph.position = point(cursor, scaled.ascent());
        cursor += scaled.h_advance(glyph.id);
        previous = Some(glyph.id);
        glyphs.push(glyph);
    }

    let bounds = glyphs
        .iter()
        .filter_map(|glyph| {
            font.outline_glyph(glyph.clone())
                .map(|item| item.px_bounds())
        })
        .reduce(union_rect)
        .unwrap_or(Rect {
            min: point(0.0, 0.0),
            max: point(cursor, size),
        });
    (glyphs, bounds)
}

fn union_rect(left: Rect, right: Rect) -> Rect {
    Rect {
        min: point(left.min.x.min(right.min.x), left.min.y.min(right.min.y)),
        max: point(left.max.x.max(right.max.x), left.max.y.max(right.max.y)),
    }
}

fn draw_glyphs(pixels: &mut [u8], font: &FontArc, glyphs: &[Glyph], color: [u8; 4]) {
    for glyph in glyphs {
        let Some(outlined) = font.outline_glyph(glyph.clone()) else {
            continue;
        };
        let bounds = outlined.px_bounds();
        outlined.draw(|x, y, coverage| {
            let px = bounds.min.x as i32 + x as i32;
            let py = bounds.min.y as i32 + y as i32;
            if px < 0 || py < 0 || px >= ICON_SIZE as i32 || py >= ICON_SIZE as i32 {
                return;
            }
            let offset = ((py as u32 * ICON_SIZE + px as u32) * 4) as usize;
            blend(&mut pixels[offset..offset + 4], color, coverage);
        });
    }
}

fn blend(destination: &mut [u8], color: [u8; 4], coverage: f32) {
    let source_alpha = coverage * color[3] as f32 / 255.0;
    let destination_alpha = destination[3] as f32 / 255.0;
    let output_alpha = source_alpha + destination_alpha * (1.0 - source_alpha);
    if output_alpha <= f32::EPSILON {
        return;
    }
    for channel in 0..3 {
        let source = color[channel] as f32 / 255.0;
        let existing = destination[channel] as f32 / 255.0;
        let output = (source * source_alpha + existing * destination_alpha * (1.0 - source_alpha))
            / output_alpha;
        destination[channel] = (output * 255.0).round() as u8;
    }
    destination[3] = (output_alpha * 255.0).round() as u8;
}

fn draw_fallback(pixels: &mut [u8], light_glyphs: bool) {
    let color = if light_glyphs {
        [250, 252, 255, 255]
    } else {
        [78, 130, 232, 255]
    };
    for y in 5..27 {
        for x in 14..18 {
            let offset = ((y * ICON_SIZE + x) * 4) as usize;
            pixels[offset..offset + 4].copy_from_slice(&color);
        }
    }
}

pub fn position_panel(window: &WebviewWindow, cursor_x: f64, cursor_y: f64) -> tauri::Result<()> {
    let monitor = window
        .monitor_from_point(cursor_x, cursor_y)?
        .or(window.current_monitor()?)
        .or(window.primary_monitor()?);
    let Some(monitor) = monitor else {
        // Displays can temporarily disappear while docking or resuming.
        return Ok(());
    };
    let configuration = &window.app_handle().config().app.windows;
    let Some(panel) = configuration.iter().find(|panel| panel.label == "main") else {
        return Ok(());
    };
    // Predict the destination size for positioning only. Resizing before moving
    // would let WM_DPICHANGED apply the monitor's scale a second time.
    let logical = panel_logical_size(
        panel.width,
        panel_content_height(panel.height),
        text_scale_factor(),
    );
    let size = panel_size(logical.width, logical.height, monitor.scale_factor());
    let area = monitor.work_area();
    let fallback = (
        area.position.x,
        area.position.y,
        area.position.x + area.size.width as i32,
        area.position.y + area.size.height as i32,
    );
    let (x, y) = panel_position(
        work_area(cursor_x as i32, cursor_y as i32).unwrap_or(fallback),
        size.width as i32,
        size.height as i32,
    );
    let position = PhysicalPosition::new(x, y);
    if window.outer_position()? != position {
        window.set_position(Position::Physical(position))?;
    }
    repair_panel_size(window)
}

fn panel_size(width: f64, height: f64, scale_factor: f64) -> PhysicalSize<u32> {
    PhysicalSize::new(
        (width * scale_factor).round() as u32,
        (height * scale_factor).round() as u32,
    )
}

fn panel_logical_size(width: f64, height: f64, text_scale: f64) -> LogicalSize<f64> {
    let scale = text_scale * PANEL_UI_SCALE;
    LogicalSize::new(width * scale, height * scale)
}

#[cfg(windows)]
fn text_scale_factor() -> f64 {
    use winreg::{enums::HKEY_CURRENT_USER, RegKey};

    // WebView2 includes Windows accessibility text scaling in devicePixelRatio,
    // while the native window's DPI does not. Include it alongside the compact
    // application scale so the CSS viewport and bottom controls remain intact.
    RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey(r"Software\Microsoft\Accessibility")
        .and_then(|key| key.get_value::<u32, _>("TextScaleFactor"))
        .map(|percent| percent.clamp(100, 225) as f64 / 100.0)
        .unwrap_or(1.0)
}

#[cfg(not(windows))]
fn text_scale_factor() -> f64 {
    1.0
}

fn repair_panel_size(window: &WebviewWindow) -> tauri::Result<()> {
    let configuration = &window.app_handle().config().app.windows;
    let Some(panel) = configuration.iter().find(|panel| panel.label == "main") else {
        return Ok(());
    };
    // Read the window's DPI AFTER moving it. Keep the configured min/max bounds
    // logical so they remain valid when another monitor uses a different DPI.
    let scale_factor = window.scale_factor()?;
    let logical = panel_logical_size(
        panel.width,
        panel_content_height(panel.height),
        text_scale_factor(),
    );
    let expected = panel_size(logical.width, logical.height, scale_factor);
    let actual = window.inner_size()?;
    if actual != expected {
        // Update both bounds in logical units when accessibility scaling changes.
        window.set_max_size(None::<LogicalSize<f64>>)?;
        window.set_min_size(Some(logical))?;
        window.set_max_size(Some(logical))?;
        window.set_size(logical)?;
        crate::logging::info(format!(
            "panel size restored: {}x{} -> {}x{}, scale={scale_factor}",
            actual.width, actual.height, expected.width, expected.height
        ));
    }
    // Reset the complete bounds, including the origin and auto-resize ratios.
    // Size equality alone cannot detect stale WebView placement after docking.
    // Use the actual client size so the WebView always fills its parent exactly.
    let webview: &tauri::Webview = window.as_ref();
    webview.set_bounds(tauri::Rect {
        position: PhysicalPosition::new(0, 0).into(),
        size: window.inner_size()?.into(),
    })?;
    window.set_zoom(PANEL_UI_SCALE)?;
    Ok(())
}

pub fn repair_panel(window: &WebviewWindow) -> tauri::Result<()> {
    let position = window.outer_position()?;
    // The center of an incorrectly scaled window can lie on a different screen.
    // Its top-left corner remains inside the monitor chosen when opening it.
    position_panel(window, position.x as f64 + 1.0, position.y as f64 + 1.0)
}

fn panel_position(work_area: (i32, i32, i32, i32), width: i32, height: i32) -> (i32, i32) {
    let (left, top, right, bottom) = work_area;
    let margin = 6;
    let x = if width + margin * 2 <= right - left {
        right - width - margin
    } else {
        left
    };
    let y = if height + margin * 2 <= bottom - top {
        bottom - height - margin
    } else {
        top
    };
    (x, y)
}

#[cfg(windows)]
pub fn cursor_position() -> (f64, f64) {
    use windows::Win32::{Foundation::POINT, UI::WindowsAndMessaging::GetCursorPos};
    let mut point = POINT::default();
    unsafe {
        if GetCursorPos(&mut point).is_ok() {
            return (point.x as f64, point.y as f64);
        }
    }
    (0.0, 0.0)
}

#[cfg(not(windows))]
pub fn cursor_position() -> (f64, f64) {
    (0.0, 0.0)
}

#[cfg(windows)]
fn work_area(x: i32, y: i32) -> Option<(i32, i32, i32, i32)> {
    use windows::Win32::{
        Foundation::POINT,
        Graphics::Gdi::{GetMonitorInfoW, MonitorFromPoint, MONITORINFO, MONITOR_DEFAULTTONEAREST},
    };

    unsafe {
        let monitor = MonitorFromPoint(POINT { x, y }, MONITOR_DEFAULTTONEAREST);
        let mut info = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if GetMonitorInfoW(monitor, &mut info).as_bool() {
            Some((
                info.rcWork.left,
                info.rcWork.top,
                info.rcWork.right,
                info.rcWork.bottom,
            ))
        } else {
            None
        }
    }
}

#[cfg(not(windows))]
fn work_area(_x: i32, _y: i32) -> Option<(i32, i32, i32, i32)> {
    None
}

#[cfg(test)]
mod tests {
    use super::{panel_logical_size, panel_position, panel_size, render};

    fn ink_size(image: &tauri::image::Image<'_>) -> (u32, u32) {
        let mut left = image.width();
        let mut top = image.height();
        let mut right = 0;
        let mut bottom = 0;
        for y in 0..image.height() {
            for x in 0..image.width() {
                let alpha = image.rgba()[((y * image.width() + x) * 4 + 3) as usize];
                if alpha < 16 {
                    continue;
                }
                left = left.min(x);
                top = top.min(y);
                right = right.max(x);
                bottom = bottom.max(y);
            }
        }
        (
            right.saturating_sub(left) + 1,
            bottom.saturating_sub(top) + 1,
        )
    }

    #[test]
    fn renders_all_numeric_widths() {
        for value in [None, Some(8), Some(10), Some(73), Some(100)] {
            let image = render(value, false);
            assert_eq!(image.width(), 32);
            assert_eq!(image.height(), 32);
            assert_eq!(image.rgba().len(), 32 * 32 * 4);
        }
    }

    #[test]
    fn anchors_panel_to_work_area_bottom_right() {
        assert_eq!(panel_position((0, 0, 1186, 847), 665, 823), (515, 18));
    }

    #[test]
    fn panel_keeps_logical_size_across_dpi_changes() {
        for (scale, width, height) in [
            (1.0, 380, 470),
            (1.25, 475, 588),
            (1.75, 665, 823),
            (2.0, 760, 940),
            (2.5, 950, 1175),
            (1.0, 380, 470),
        ] {
            let size = panel_size(380.0, 470.0, scale);
            assert_eq!((size.width, size.height), (width, height));
        }
    }

    #[test]
    fn accessibility_text_scaling_preserves_css_viewport_on_both_displays() {
        let logical = panel_logical_size(320.0, 470.0, 1.2);
        assert_eq!(panel_size(logical.width, logical.height, 1.75).width, 571);
        assert_eq!(panel_size(logical.width, logical.height, 1.75).height, 839);
        assert_eq!(panel_size(logical.width, logical.height, 1.5).width, 490);
        assert_eq!(panel_size(logical.width, logical.height, 1.5).height, 719);
    }

    #[test]
    fn restored_panel_fits_secondary_monitor_work_area() {
        let size = panel_size(380.0, 470.0, 2.0);
        assert_eq!(
            panel_position(
                (-1920, -120, 0, 1040),
                size.width as i32,
                size.height as i32
            ),
            (-766, 94)
        );
    }

    #[test]
    fn two_digit_value_uses_most_of_the_tray_canvas() {
        let image = render(Some(33), false);
        let (width, height) = ink_size(&image);
        assert!(width >= 28, "ink width was only {width}");
        assert!(height >= 20, "ink height was only {height}");
    }
}
