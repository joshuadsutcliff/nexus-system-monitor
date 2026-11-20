use crate::{DiskCategory, DiskNode};
use compact_str::CompactString;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct RectBounds {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TreemapRect {
    pub name: CompactString,
    pub full_path: CompactString,
    pub size: u64,
    pub is_dir: bool,
    pub category: DiskCategory,
    pub depth: u32,
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

const MIN_RECT_SIZE: f32 = 3.0;
const MAX_DEPTH: u32 = 2;
const MAX_RECTS: usize = 8_000;

/// Computes a squarified treemap layout over `nodes` within `rect`.
/// Filters zero-size nodes, caps depth at `2` and total rectangles at `8,000`,
/// and minimizes worst aspect ratio per row.
pub fn compute_squarified_treemap(nodes: &[DiskNode], rect: RectBounds) -> Vec<TreemapRect> {
    let mut out = Vec::with_capacity(256);
    let total_size: u64 = nodes.iter().map(|n| n.size).sum();
    if total_size == 0 || rect.width < MIN_RECT_SIZE || rect.height < MIN_RECT_SIZE {
        return out;
    }
    layout_children(nodes, rect, total_size, &mut out, 0);
    out
}

fn layout_children(
    children: &[DiskNode],
    bounds: RectBounds,
    total_size: u64,
    output: &mut Vec<TreemapRect>,
    depth: u32,
) {
    if depth >= MAX_DEPTH || output.len() >= MAX_RECTS || total_size == 0 {
        return;
    }
    if bounds.width < MIN_RECT_SIZE || bounds.height < MIN_RECT_SIZE {
        return;
    }
    let mut nodes: Vec<&DiskNode> = children.iter().filter(|n| n.size > 0).collect();
    if nodes.is_empty() {
        return;
    }
    nodes.sort_by_key(|a| std::cmp::Reverse(a.size));

    let mut remaining = nodes;
    let mut current_bounds = bounds;
    let mut remaining_size = total_size;

    while !remaining.is_empty()
        && current_bounds.width >= MIN_RECT_SIZE
        && current_bounds.height >= MIN_RECT_SIZE
        && output.len() < MAX_RECTS
    {
        let horizontal = current_bounds.width >= current_bounds.height;
        let strip = if horizontal {
            current_bounds.height
        } else {
            current_bounds.width
        };

        let row_len = build_row_len(&remaining, strip, remaining_size, current_bounds);
        let (row, rest) = remaining.split_at(row_len);
        place_row(
            row,
            current_bounds,
            remaining_size,
            horizontal,
            output,
            depth,
        );

        let row_size: u64 = row.iter().map(|n| n.size).sum();
        remaining_size = remaining_size.saturating_sub(row_size);
        if remaining_size == 0 || rest.is_empty() {
            break;
        }

        let frac = (row_size as f64 / (row_size + remaining_size) as f64) as f32;
        if horizontal {
            let used_w = current_bounds.width * frac;
            current_bounds = RectBounds {
                x: current_bounds.x + used_w,
                y: current_bounds.y,
                width: (current_bounds.width - used_w).max(0.0),
                height: current_bounds.height,
            };
        } else {
            let used_h = current_bounds.height * frac;
            current_bounds = RectBounds {
                x: current_bounds.x,
                y: current_bounds.y + used_h,
                width: current_bounds.width,
                height: (current_bounds.height - used_h).max(0.0),
            };
        }
        remaining = rest.to_vec();
    }
}

fn build_row_len(nodes: &[&DiskNode], strip: f32, total_size: u64, bounds: RectBounds) -> usize {
    let mut best_ratio = f64::MAX;
    let mut count = 0usize;
    for i in 0..nodes.len() {
        let candidate = &nodes[..=i];
        let ratio = worst_ratio(candidate, strip, total_size, bounds);
        if count > 0 && ratio > best_ratio {
            return count;
        }
        best_ratio = ratio;
        count += 1;
    }
    count.max(1)
}

fn worst_ratio(row: &[&DiskNode], strip: f32, total_size: u64, bounds: RectBounds) -> f64 {
    let row_size: u64 = row.iter().map(|n| n.size).sum();
    if row_size == 0 || total_size == 0 || strip <= 0.0 {
        return f64::MAX;
    }
    let total_area = f64::from(bounds.width) * f64::from(bounds.height);
    let row_area = total_area * (row_size as f64 / total_size as f64);
    let strip_len = row_area / f64::from(strip);
    if strip_len <= 0.0 {
        return f64::MAX;
    }
    let mut worst = 0.0_f64;
    for n in row {
        let cell_area = total_area * (n.size as f64 / total_size as f64);
        let h = cell_area / strip_len;
        if h <= 0.0 {
            continue;
        }
        let ratio = (strip_len / h).max(h / strip_len);
        if ratio > worst {
            worst = ratio;
        }
    }
    worst
}

fn place_row(
    row: &[&DiskNode],
    bounds: RectBounds,
    total_size: u64,
    horizontal: bool,
    output: &mut Vec<TreemapRect>,
    depth: u32,
) {
    let row_size: u64 = row.iter().map(|n| n.size).sum();
    if row_size == 0 || total_size == 0 {
        return;
    }
    let row_frac = (row_size as f64 / total_size as f64) as f32;

    if horizontal {
        let row_w = bounds.width * row_frac;
        let mut cell_y = bounds.y;
        for node in row {
            if output.len() >= MAX_RECTS {
                break;
            }
            let cell_h = bounds.height * (node.size as f64 / row_size as f64) as f32;
            if row_w >= MIN_RECT_SIZE && cell_h >= MIN_RECT_SIZE {
                output.push(TreemapRect {
                    name: node.name.clone(),
                    full_path: node.full_path.clone(),
                    size: node.size,
                    is_dir: node.is_dir,
                    category: node.category,
                    depth,
                    x: bounds.x,
                    y: cell_y,
                    width: row_w,
                    height: cell_h,
                });
                if node.is_dir && !node.children.is_empty() && output.len() < MAX_RECTS {
                    let inner = RectBounds {
                        x: bounds.x + 1.0,
                        y: cell_y + 14.0,
                        width: (row_w - 2.0).max(0.0),
                        height: (cell_h - 15.0).max(0.0),
                    };
                    if inner.width > MIN_RECT_SIZE && inner.height > MIN_RECT_SIZE {
                        layout_children(&node.children, inner, node.size, output, depth + 1);
                    }
                }
            }
            cell_y += cell_h;
        }
    } else {
        let row_h = bounds.height * row_frac;
        let mut cell_x = bounds.x;
        for node in row {
            if output.len() >= MAX_RECTS {
                break;
            }
            let cell_w = bounds.width * (node.size as f64 / row_size as f64) as f32;
            if cell_w >= MIN_RECT_SIZE && row_h >= MIN_RECT_SIZE {
                output.push(TreemapRect {
                    name: node.name.clone(),
                    full_path: node.full_path.clone(),
                    size: node.size,
                    is_dir: node.is_dir,
                    category: node.category,
                    depth,
                    x: cell_x,
                    y: bounds.y,
                    width: cell_w,
                    height: row_h,
                });
                if node.is_dir && !node.children.is_empty() && output.len() < MAX_RECTS {
                    let inner = RectBounds {
                        x: cell_x + 1.0,
                        y: bounds.y + 14.0,
                        width: (cell_w - 2.0).max(0.0),
                        height: (row_h - 15.0).max(0.0),
                    };
                    if inner.width > MIN_RECT_SIZE && inner.height > MIN_RECT_SIZE {
                        layout_children(&node.children, inner, node.size, output, depth + 1);
                    }
                }
            }
            cell_x += cell_w;
        }
    }
}
