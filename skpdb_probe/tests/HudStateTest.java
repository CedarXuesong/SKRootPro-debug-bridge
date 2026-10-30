import skpdb.cursor.HudState;

public final class HudStateTest {
    static void check(boolean value) { if (!value) throw new AssertionError(); }
    static HudState parse(String text, long now) { return new HudState(text.split("\\s+"), now); }
    static void bad(String text) {
        try { parse(text, 0); throw new AssertionError("Accepted malformed HUD"); }
        catch (IllegalArgumentException expected) { }
    }
    public static void main(String[] args) {
        for (int rotation = 0; rotation < 4; rotation++) {
            for (int mode = 0; mode < 3; mode++) {
                HudState s = parse("hud 99 1 " + rotation + " 2400 1080 8 10 4 2398 24 " + mode + " 0", 123);
                check(s.sequence == 99 && s.rotation == rotation && s.active == 8 && s.capacity == 10 && s.pending == 4);
                check(s.visible(123) && s.visible(123 + HudState.LEASE_NANOS - 1) && !s.visible(123 + HudState.LEASE_NANOS));
                check(s.lines()[1].contains("输入暂停") && s.lines()[4].contains("239.8") && s.lines()[4].contains("2.4"));
                check(s.bottomY() > 0 && s.bottomY() + 46 * s.scale() < s.height && 560 * s.scale() < s.width);
            }
        }
        check(!parse("hud 1 0 0 1080 2400 0 10 0 0 0 0 1", 0).visible(1));
        bad("hud 1 1 4 1080 2400 0 10 0 0 0 0 1");
        bad("hud 1 1 0 0 2400 0 10 0 0 0 0 1");
        bad("hud 1 1 0 1080 2400 11 10 0 0 0 0 1");
        bad("hud 1 1 0 1080 2400 0 10 0 0 0 3 1");
        bad("hud 1 1 0 1080 2400 0 10 0 0 0 0 2");
        bad("hud 1 1 0 1080 2400 -1 10 0 0 0 0 1");
        bad("hud 1 1 0 1080 2400 0 10 0 65536 0 0 1");
        bad("hud 1");
        System.out.println("PASS HUD 12 rotation/mode combinations, placement, text, lease expiration and malformed inputs");
    }
}
