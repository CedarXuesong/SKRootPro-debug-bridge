"""Compare matching hardware timestamps, removing the phone's physical motion."""
import math
import statistics


def pairs(target, control, sensor_type):
    original = {row[0]: row for row in control if row[1] == sensor_type}
    return [(row, original[row[0]]) for row in target if row[1] == sensor_type and row[0] in original]


def raw_delta(target, control, sensor_type):
    matched = pairs(target, control, sensor_type)
    if len(matched) < 10:
        raise ValueError(f"type {sensor_type}: 同时间戳样本不足 ({len(matched)})")
    return [statistics.fmean(a[i + 2] - b[i + 2] for a, b in matched) for i in range(3)], len(matched)


def multiply(a, b):
    x, y, z, w = a
    X, Y, Z, W = b
    return (w*X + x*W + y*Z - z*Y, w*Y - x*Z + y*W + z*X,
            w*Z + x*Y - y*X + z*W, w*W - x*X - y*Y - z*Z)


def normalized(q):
    q = list(q)
    if q[3] == 0:
        q[3] = math.sqrt(max(0, 1 - sum(x*x for x in q[:3])))
    norm = math.sqrt(sum(x*x for x in q))
    if not math.isfinite(norm) or norm < 1e-6:
        raise ValueError("Invalid rotation vector")
    return tuple(x / norm for x in q)


def conjugate(q):
    return (-q[0], -q[1], -q[2], q[3])


def fusion_rate(target, control, sensor_type):
    matched = pairs(target, control, sensor_type)
    if len(matched) < 10:
        raise ValueError(f"type {sensor_type}: 同时间戳旋转向量不足 ({len(matched)})")
    first, last = matched[0], matched[-1]
    elapsed = (last[0][0] - first[0][0]) * 1e-9
    if elapsed < .2:
        raise ValueError("Rotation vector window is too short")
    relative = [multiply(conjugate(normalized(b[2:6])), normalized(a[2:6])) for a, b in (first, last)]
    change = multiply(conjugate(relative[0]), relative[1])
    if change[3] < 0:
        change = tuple(-x for x in change)
    magnitude = math.sqrt(sum(x*x for x in change[:3]))
    scale = 2 * math.atan2(magnitude, change[3]) / magnitude / elapsed if magnitude > 1e-8 else 0
    return [x * scale for x in change[:3]], len(matched)
