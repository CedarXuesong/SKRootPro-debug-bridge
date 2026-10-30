package skpdb.cursor;

import java.util.Locale;

/** Immutable protocol state, kept Android-independent for offline regression. */
public final class HudState {
    public static final long LEASE_NANOS = 6_000_000_000L;
    public final long sequence, received;
    public final int enabled, rotation, width, height, active, capacity, pending, hz, rtt, mode, input;

    public HudState(String[] parts, long now) {
        if (parts.length != 13 || !parts[0].equals("hud")) throw new IllegalArgumentException("HUD length");
        sequence = Long.parseLong(parts[1]); received = now;
        int[] v = new int[11];
        for (int i = 0; i < v.length; i++) {
            v[i] = Integer.parseInt(parts[i + 2]);
            if (v[i] < 0 || v[i] > 65535) throw new IllegalArgumentException("HUD range");
        }
        enabled = v[0]; rotation = v[1]; width = v[2]; height = v[3];
        active = v[4]; capacity = v[5]; pending = v[6]; hz = v[7]; rtt = v[8]; mode = v[9]; input = v[10];
        if (enabled > 1 || rotation > 3 || width == 0 || height == 0 || capacity == 0 ||
                active > capacity || mode > 2 || input > 1) throw new IllegalArgumentException("HUD fields");
    }

    public boolean visible(long now) { return enabled == 1 && now - received < LEASE_NANOS; }
    public float scale() { return Math.max(.25f, Math.min(1.5f, Math.min(width, height) / 720f)); }
    public float bottomY() { return Math.max(0, height - 62 * scale()); }
    public String[] lines() {
        return new String[] { "SKPDB 调试 0.12.13",
            new String[] { "鼠标模式", "视角模式", "按住观察" }[mode] + (input == 1 ? " · 输入活动" : " · 输入暂停"),
            width + " × " + height + " · 旋转 " + rotation * 90 + "°",
            "触点 " + active + "/" + capacity + " · 等待 " + pending,
            String.format(Locale.ROOT, "采样 %.1f Hz · RTT %.1f ms", hz / 10.0, rtt / 10.0) };
    }
}
